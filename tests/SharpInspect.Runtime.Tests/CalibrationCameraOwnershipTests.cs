using System.Collections.Concurrent;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationCameraOwnershipTests
{
    private const string Role = "Primary";

    [Fact]
    public async Task V124_O01_BaselineTemporaryCaptureAndRestoreUseOneExclusiveOwner()
    {
        await using var fixture = Fixture.Create();
        await fixture.Service.RefreshAsync();

        var begin = await fixture.Service.BeginCalibrationConfigurationAsync(
            Guid.NewGuid(), fixture.TemporaryRequested);

        Assert.True(begin.Succeeded, begin.ReasonCode);
        var lease = Assert.IsType<CameraCalibrationLease>(begin.Lease);
        Assert.Equal(fixture.Target, lease.BaselineTarget);
        Assert.Equal(Role, lease.LogicalCameraRole);
        Assert.Equal(fixture.BaselineRequested, lease.BaselineRequested);
        Assert.Equal(fixture.BaselineEffective, lease.BaselineEffective);
        Assert.Equal(fixture.TemporaryRequested, lease.TemporaryRequested);
        Assert.Equal(fixture.TemporaryEffective, lease.TemporaryEffective);

        var qualificationWhileOwned = await fixture.Service.AcquireAsync(
            ExecutionKind.Qualification, Role);
        Assert.False(qualificationWhileOwned.Accepted);
        Assert.Equal("CameraCalibrationActive", qualificationWhileOwned.ReasonCode);

        var frameId = Guid.NewGuid();
        var capture = await lease.CaptureAsync(frameId);
        Assert.True(capture.Accepted, capture.ReasonCode);
        Assert.Equal(ExecutionKind.Calibration, capture.Correlation?.Kind);
        Assert.Equal(frameId, capture.Correlation?.Value);
        var frameLease = capture.Outcome!.TakeFrame();
        Assert.NotNull(frameLease);
        Assert.Equal(frameId, frameLease!.Frame.Correlation.Value);
        frameLease.Dispose();

        var restore = await lease.RestoreAsync();
        Assert.True(restore.Succeeded, restore.ReasonCode);
        Assert.Equal(CameraRecoveryState.Healthy, fixture.Service.GetSnapshot().State);
        Assert.True(fixture.Service.GetSnapshot().SourceHealthy);
        Assert.Equal(2, fixture.Provider.OpenCalls);
        Assert.Equal(1, fixture.Initial.StopCalls);
        Assert.Equal(1, fixture.Initial.DisposeCalls);
        Assert.Equal(1, fixture.TemporaryDevice!.ApplyCalls);
        Assert.Equal(1, fixture.TemporaryDevice!.StartCalls);
        Assert.Equal(1, fixture.TemporaryDevice!.StopCalls);
        Assert.Equal(1, fixture.TemporaryDevice!.DisposeCalls);
        Assert.Equal(1, fixture.RestoredDevice!.ApplyCalls);
        Assert.Equal(1, fixture.RestoredDevice!.StartCalls);

        var afterRestore = await fixture.Service.AcquireAsync(ExecutionKind.Qualification, Role);
        Assert.True(afterRestore.Accepted, afterRestore.ReasonCode);
        afterRestore.Outcome?.Dispose();
    }

    [Fact]
    public async Task V124_O02_SecondCalibrationSessionAndQualificationAreRejectedWhileLeaseIsHeld()
    {
        await using var fixture = Fixture.Create();
        await fixture.Service.RefreshAsync();
        var first = await fixture.Service.BeginCalibrationConfigurationAsync(
            Guid.NewGuid(), fixture.TemporaryRequested);
        Assert.True(first.Succeeded, first.ReasonCode);

        var second = await fixture.Service.BeginCalibrationConfigurationAsync(
            Guid.NewGuid(), fixture.TemporaryRequested);
        Assert.False(second.Succeeded);
        Assert.Equal("CameraCalibrationSessionConflict", second.ReasonCode);

        var baselineWhileOwned = await fixture.Service.GetCalibrationBaselineSnapshot();
        Assert.False(baselineWhileOwned.Succeeded);
        Assert.Equal("CameraCalibrationSessionConflict", baselineWhileOwned.ReasonCode);

        var qualification = await fixture.Service.AcquireAsync(
            ExecutionKind.Qualification, Role);
        Assert.False(qualification.Accepted);
        Assert.Equal("CameraCalibrationActive", qualification.ReasonCode);

        var lease = Assert.IsType<CameraCalibrationLease>(first.Lease);
        var restored = await lease.RestoreAsync();
        Assert.True(restored.Succeeded, restored.ReasonCode);
    }

    [Fact]
    public async Task V124_O03_PublicCalibrationKindIsRejectedWithoutAllocatingAFrameCorrelation()
    {
        await using var fixture = Fixture.Create();
        await fixture.Service.RefreshAsync();

        var result = await fixture.Service.AcquireAsync(ExecutionKind.Calibration, Role);

        Assert.False(result.Accepted);
        Assert.Null(result.Correlation);
        Assert.Equal("ProductionAcquisitionUnavailable", result.ReasonCode);
        Assert.Equal(0, fixture.Initial.AcquireCalls);
    }

    [Fact]
    public async Task V124_O04_RestoreWaitsForCapturedFrameLeaseBeforeRetiringTemporaryOwner()
    {
        await using var fixture = Fixture.Create(operationTimeout: TimeSpan.FromMilliseconds(35));
        await fixture.Service.RefreshAsync();
        var begin = await fixture.Service.BeginCalibrationConfigurationAsync(
            Guid.NewGuid(), fixture.TemporaryRequested);
        var lease = Assert.IsType<CameraCalibrationLease>(begin.Lease);

        var capture = await lease.CaptureAsync(Guid.NewGuid());
        Assert.True(capture.Accepted, capture.ReasonCode);
        var frameLease = capture.Outcome!.TakeFrame();
        Assert.NotNull(frameLease);

        var blockedRestore = await lease.RestoreAsync();
        Assert.False(blockedRestore.Succeeded);
        Assert.Equal("CameraCalibrationRetirementTimeout", blockedRestore.ReasonCode);
        Assert.True(fixture.Service.IsCalibrationActive);
        Assert.Equal(1, fixture.Provider.OpenCalls);
        Assert.Equal(0, fixture.TemporaryDevice!.StopCalls);
        Assert.Equal(0, fixture.TemporaryDevice!.DisposeCalls);

        frameLease!.Dispose();
        var restored = await lease.RestoreAsync();
        Assert.True(restored.Succeeded, restored.ReasonCode);
        Assert.Equal(1, fixture.TemporaryDevice!.StopCalls);
        Assert.Equal(1, fixture.TemporaryDevice!.DisposeCalls);
        Assert.Equal(2, fixture.Provider.OpenCalls);
    }

    [Fact]
    public async Task V124_O05_RestoreFailureKeepsSessionBlockedUntilACompleteRetrySucceeds()
    {
        await using var fixture = Fixture.Create();
        var failingBaseline = fixture.RestoredDevice!;
        failingBaseline.StartHandler = _ => Task.FromResult(
            CameraOperationResult.Failure("FixtureRestoreStartFailed"));
        fixture.RestoredDevice = fixture.NewDevice();
        fixture.Provider.Enqueue(fixture.RestoredDevice!);

        await fixture.Service.RefreshAsync();
        var begin = await fixture.Service.BeginCalibrationConfigurationAsync(
            Guid.NewGuid(), fixture.TemporaryRequested);
        var lease = Assert.IsType<CameraCalibrationLease>(begin.Lease);

        var failedRestore = await lease.RestoreAsync();
        Assert.False(failedRestore.Succeeded);
        Assert.Equal("CameraCalibrationStartFailed", failedRestore.ReasonCode);
        Assert.True(fixture.Service.IsCalibrationActive);
        Assert.False(fixture.Service.GetSnapshot().SourceHealthy);
        var blockedAcquire = await fixture.Service.AcquireAsync(
            ExecutionKind.Qualification, Role);
        Assert.False(blockedAcquire.Accepted);
        Assert.Equal("CameraCalibrationActive", blockedAcquire.ReasonCode);
        Assert.Equal(1, failingBaseline.StopCalls);
        Assert.Equal(1, failingBaseline.DisposeCalls);

        var restored = await lease.RestoreAsync();
        Assert.True(restored.Succeeded, restored.ReasonCode);
        Assert.False(fixture.Service.IsCalibrationActive);
        Assert.Equal(CameraRecoveryState.Healthy, fixture.Service.GetSnapshot().State);
        Assert.Equal(3, fixture.Provider.OpenCalls);
        Assert.Equal(1, fixture.RestoredDevice!.StartCalls);
    }

    [Fact]
    public async Task V124_O06_LateOpenAfterBoundedCancellationRemainsOwnedUntilRetirement()
    {
        await using var fixture = Fixture.Create(
            operationTimeout: TimeSpan.FromMilliseconds(30),
            shutdownWaitTimeout: TimeSpan.FromMilliseconds(30));
        var openCompletion = new TaskCompletionSource<CameraOpenResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateDevice = fixture.NewDevice();
        fixture.Provider.OpenHandler = (_, _) => openCompletion.Task;
        var sessionId = Guid.NewGuid();

        await fixture.Service.RefreshAsync();
        var begin = await fixture.Service.BeginCalibrationConfigurationAsync(
            sessionId, fixture.TemporaryRequested);
        Assert.False(begin.Succeeded);
        Assert.Equal("CameraCalibrationOpenFailed", begin.ReasonCode);
        Assert.Equal(sessionId, begin.SessionId);
        Assert.True(fixture.Service.IsCalibrationActive);
        Assert.Equal(1, fixture.Provider.OpenCalls);

        await fixture.Service.DisposeAsync();
        Assert.Equal(0, fixture.Provider.DisposeCalls);

        openCompletion.SetResult(CameraOpenResult.Success(lateDevice));
        var retired = await fixture.Service.RetireAsync();

        Assert.True(retired.SafeToReplace, retired.ReasonCode);
        Assert.Equal(1, lateDevice.StopCalls);
        Assert.Equal(1, lateDevice.DisposeCalls);
        Assert.Equal(1, fixture.Provider.DisposeCalls);
    }

    [Fact]
    public async Task V124_O07_CancelledApplyKeepsThePhysicalOwnerUntilExplicitRetirement()
    {
        await using var fixture = Fixture.Create(operationTimeout: TimeSpan.FromSeconds(1));
        var applyCompletion = new TaskCompletionSource<CameraConfigurationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.TemporaryDevice!.ApplyHandler = _ => applyCompletion.Task;
        using var cancellation = new CancellationTokenSource();
        var sessionId = Guid.NewGuid();

        await fixture.Service.RefreshAsync();
        var beginTask = fixture.Service.BeginCalibrationConfigurationAsync(
            sessionId, fixture.TemporaryRequested, cancellation.Token);
        await fixture.TemporaryDevice!.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var begin = await beginTask;
        Assert.False(begin.Succeeded);
        Assert.Equal("CameraCalibrationCancelled", begin.ReasonCode);
        Assert.Equal(sessionId, begin.SessionId);
        Assert.True(fixture.Service.IsCalibrationActive);
        var blocked = await fixture.Service.AcquireAsync(ExecutionKind.Qualification, Role);
        Assert.False(blocked.Accepted);
        Assert.Equal("CameraCalibrationActive", blocked.ReasonCode);

        applyCompletion.SetResult(CameraConfigurationResult.Success(fixture.TemporaryEffective));
        var retired = await fixture.Service.RetireAsync();
        Assert.True(retired.SafeToReplace, retired.ReasonCode);
        Assert.Equal(1, fixture.TemporaryDevice!.StopCalls);
        Assert.Equal(1, fixture.TemporaryDevice!.DisposeCalls);
    }

    [Fact]
    public async Task V124_O08_PersistedBaselineRestoreForcesAFullReopenEvenWhenSeedIsHealthy()
    {
        await using var fixture = Fixture.Create();

        var baseline = await fixture.Service.GetCalibrationBaselineSnapshot();
        Assert.True(baseline.Succeeded, baseline.ReasonCode);
        var snapshot = Assert.IsType<CameraCalibrationBaselineSnapshot>(baseline.Snapshot);
        Assert.Equal(fixture.Target, snapshot.Target);
        Assert.Equal(Role, snapshot.LogicalCameraRole);
        Assert.Equal(fixture.BaselineRequested, snapshot.Requested);
        Assert.Equal(fixture.BaselineEffective, snapshot.Effective);
        Assert.Equal(CameraAcquisitionState.Armed, snapshot.Health.Acquisition);
        Assert.True(fixture.Initial.HealthCalls >= 1);

        var restored = await fixture.Service.RestorePersistedCalibrationBaselineAsync(
            Guid.NewGuid(), snapshot);

        Assert.True(restored.Succeeded, restored.ReasonCode);
        Assert.Equal(CameraRecoveryState.Healthy, fixture.Service.GetSnapshot().State);
        Assert.True(fixture.Service.GetSnapshot().SourceHealthy);
        Assert.Equal(1, fixture.Provider.OpenCalls);
        Assert.Equal(1, fixture.Initial.StopCalls);
        Assert.Equal(1, fixture.Initial.DisposeCalls);
        Assert.Equal(1, fixture.TemporaryDevice!.ApplyCalls);
        Assert.Equal(1, fixture.TemporaryDevice!.StartCalls);
        Assert.Equal(fixture.BaselineEffective, fixture.TemporaryDevice!.CurrentEffective);

        var qualification = await fixture.Service.AcquireAsync(
            ExecutionKind.Qualification, Role);
        Assert.True(qualification.Accepted, qualification.ReasonCode);
        qualification.Outcome?.Dispose();
    }

    [Fact]
    public async Task V124_O09_PhysicalRestoreKeepsDurableAdmissionFenceUntilCoordinatorAcknowledgesClosure()
    {
        await using var fixture = Fixture.Create();
        await fixture.Service.RefreshAsync();

        var sessionId = Guid.NewGuid();
        Assert.True(fixture.Service.TryHoldCalibrationAdmission(sessionId, out var holdReason),
            holdReason);
        Assert.Equal("CameraCalibrationAdmissionHeld", holdReason);
        Assert.True(fixture.Service.TryHoldCalibrationAdmission(sessionId, out var repeatReason),
            repeatReason);
        Assert.Equal("CameraCalibrationAdmissionAlreadyHeld", repeatReason);

        var competing = await fixture.Service.BeginCalibrationConfigurationAsync(
            Guid.NewGuid(), fixture.TemporaryRequested);
        Assert.False(competing.Succeeded);
        Assert.Equal("CameraCalibrationSessionConflict", competing.ReasonCode);

        var begin = await fixture.Service.BeginCalibrationConfigurationAsync(
            sessionId, fixture.TemporaryRequested);
        Assert.True(begin.Succeeded, begin.ReasonCode);
        var lease = Assert.IsType<CameraCalibrationLease>(begin.Lease);

        var restored = await lease.RestoreAsync();
        Assert.True(restored.Succeeded, restored.ReasonCode);
        Assert.True(fixture.Service.IsCalibrationActive);
        Assert.Equal(sessionId, fixture.Service.CalibrationSessionId);

        var blocked = await fixture.Service.AcquireAsync(ExecutionKind.Qualification, Role);
        Assert.False(blocked.Accepted);
        Assert.Equal("CameraCalibrationActive", blocked.ReasonCode);
        Assert.False(fixture.Service.ConfirmCalibrationSessionClosed(Guid.NewGuid()));

        Assert.True(fixture.Service.ConfirmCalibrationSessionClosed(sessionId, out var closeReason),
            closeReason);
        Assert.Equal("CameraCalibrationAdmissionClosed", closeReason);
        Assert.False(fixture.Service.IsCalibrationActive);

        var qualification = await fixture.Service.AcquireAsync(
            ExecutionKind.Qualification, Role);
        Assert.True(qualification.Accepted, qualification.ReasonCode);
        qualification.Outcome?.Dispose();
    }

    [Fact]
    public async Task V124_O10_BoundedBeginAndLateOpenKeepDurableAdmissionFenceOwned()
    {
        await using var fixture = Fixture.Create(
            operationTimeout: TimeSpan.FromMilliseconds(30),
            shutdownWaitTimeout: TimeSpan.FromMilliseconds(30));
        var openCompletion = new TaskCompletionSource<CameraOpenResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateDevice = fixture.NewDevice();
        fixture.Provider.OpenHandler = (_, _) => openCompletion.Task;

        await fixture.Service.RefreshAsync();
        var sessionId = Guid.NewGuid();
        Assert.True(fixture.Service.TryHoldCalibrationAdmission(sessionId, out var holdReason),
            holdReason);

        var begin = await fixture.Service.BeginCalibrationConfigurationAsync(
            sessionId, fixture.TemporaryRequested);
        Assert.False(begin.Succeeded);
        Assert.Equal("CameraCalibrationOpenFailed", begin.ReasonCode);
        Assert.Equal(sessionId, begin.SessionId);
        Assert.True(fixture.Service.IsCalibrationActive);

        var blocked = await fixture.Service.AcquireAsync(ExecutionKind.Qualification, Role);
        Assert.False(blocked.Accepted);
        Assert.Equal("CameraCalibrationActive", blocked.ReasonCode);
        Assert.False(fixture.Service.ConfirmCalibrationSessionClosed(sessionId));

        openCompletion.SetResult(CameraOpenResult.Success(lateDevice));
        Assert.True(fixture.Service.IsCalibrationActive);
        Assert.False(fixture.Service.ConfirmCalibrationSessionClosed(sessionId));

        var retired = await fixture.Service.RetireAsync();
        Assert.True(retired.SafeToReplace, retired.ReasonCode);
        Assert.Equal(1, lateDevice.StopCalls);
        Assert.Equal(1, lateDevice.DisposeCalls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(ManualClock clock, CalibrationProvider provider,
            CameraBindingTarget target, CalibrationDevice initial,
            CameraAcquisitionService initialAcquisition, CameraRecoveryService service,
            RequestedCameraConfiguration baselineRequested,
            EffectiveCameraConfiguration baselineEffective,
            RequestedCameraConfiguration temporaryRequested,
            EffectiveCameraConfiguration temporaryEffective)
        {
            Clock = clock;
            Provider = provider;
            Target = target;
            Initial = initial;
            InitialAcquisition = initialAcquisition;
            Service = service;
            BaselineRequested = baselineRequested;
            BaselineEffective = baselineEffective;
            TemporaryRequested = temporaryRequested;
            TemporaryEffective = temporaryEffective;
        }

        internal ManualClock Clock { get; }
        internal CalibrationProvider Provider { get; }
        internal CameraBindingTarget Target { get; }
        internal CalibrationDevice Initial { get; }
        internal CameraAcquisitionService InitialAcquisition { get; }
        internal CameraRecoveryService Service { get; }
        internal RequestedCameraConfiguration BaselineRequested { get; }
        internal EffectiveCameraConfiguration BaselineEffective { get; }
        internal RequestedCameraConfiguration TemporaryRequested { get; }
        internal EffectiveCameraConfiguration TemporaryEffective { get; }
        internal CalibrationDevice? TemporaryDevice { get; set; }
        internal CalibrationDevice? RestoredDevice { get; set; }

        internal static Fixture Create(TimeSpan? operationTimeout = null,
            TimeSpan? shutdownWaitTimeout = null)
        {
            var clock = new ManualClock();
            var identity = new CameraProviderIdentity("FixtureProvider", "1",
                "FixtureAdapter", "1");
            var target = new CameraBindingTarget(identity, "FixtureDevice");
            var baselineRequested = Configuration(10);
            var baselineEffective = Capabilities().ValidateConfiguration(baselineRequested).Effective!;
            var temporaryRequested = Configuration(20);
            var temporaryEffective = Capabilities().ValidateConfiguration(temporaryRequested).Effective!;
            var initial = new CalibrationDevice(target, clock, baselineEffective)
            {
                Started = true
            };
            var provider = new CalibrationProvider(identity);
            var acquisition = new CameraAcquisitionService(initial, baselineEffective, clock,
                new CameraAcquisitionOptions(TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1), protocolReadTimeout: TimeSpan.FromSeconds(1)));
            var service = new CameraRecoveryService(provider, target, Role,
                baselineRequested, acquisition, clock, new CameraRecoveryOptions(
                    TimeSpan.FromSeconds(5), 20,
                    operationTimeout ?? TimeSpan.FromSeconds(1),
                    shutdownWaitTimeout ?? TimeSpan.FromSeconds(1)));
            var fixture = new Fixture(clock, provider, target, initial, acquisition, service,
                baselineRequested, baselineEffective, temporaryRequested, temporaryEffective);
            fixture.TemporaryDevice = fixture.NewDevice();
            provider.Enqueue(fixture.TemporaryDevice!);
            fixture.RestoredDevice = fixture.NewDevice();
            provider.Enqueue(fixture.RestoredDevice!);
            return fixture;
        }

        internal CalibrationDevice NewDevice() =>
            new(Target, Clock, BaselineEffective);

        private static RequestedCameraConfiguration Configuration(double exposure) =>
            new(ProductionAcquisitionMode.SoftwareTrigger, exposure, 0,
                new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8, null, 100, 0, null);

        private static CameraCapabilities Capabilities() => new(
            new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new CameraDoubleCapability(1, 100, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            new CameraRoiCapabilities(1, 1, new(0, 0, 1), new(0, 0, 1),
                new(1, 1, 1), new(1, 1, 1)));

        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    private sealed class CalibrationProvider : ICameraProvider
    {
        private readonly ConcurrentQueue<CalibrationDevice> _devices = new();
        private int _openCalls;
        private int _disposeCalls;

        internal CalibrationProvider(CameraProviderIdentity identity)
        {
            Identity = identity;
        }

        public CameraProviderIdentity Identity { get; }
        internal Func<string, CancellationToken, Task<CameraOpenResult>>? OpenHandler { get; set; }
        internal int OpenCalls => Volatile.Read(ref _openCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        internal void Enqueue(CalibrationDevice device) => _devices.Enqueue(device);

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("CalibrationMustNotDiscover");

        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openCalls);
            if (OpenHandler is { } handler)
                return new ValueTask<CameraOpenResult>(handler(stableDeviceIdentity,
                    cancellationToken));
            return new ValueTask<CameraOpenResult>(Task.FromResult(
                _devices.TryDequeue(out var device)
                    ? CameraOpenResult.Success(device)
                    : CameraOpenResult.Failure("FixtureOpenQueueEmpty")));
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CalibrationDevice : IControlledCameraDevice
    {
        private readonly ManualClock _clock;
        private readonly object _sync = new();
        private EffectiveCameraConfiguration _effective;
        private bool _disposed;
        private bool _started;
        private readonly Guid _protocolEpoch = Guid.NewGuid();
        private int _applyCalls;
        private int _startCalls;
        private int _stopCalls;
        private int _disposeCalls;
        private int _acquireCalls;
        private int _healthCalls;

        internal CalibrationDevice(CameraBindingTarget target, ManualClock clock,
            EffectiveCameraConfiguration effective)
        {
            _clock = clock;
            _effective = effective;
            Descriptor = new CameraDeviceDescriptor(target.Provider,
                target.StableDeviceIdentity, "Fixture camera", "Fixture model");
            Capabilities = FixtureCapabilities();
        }

        public CameraDeviceDescriptor Descriptor { get; }
        public CameraCapabilities Capabilities { get; }
        internal bool Started
        {
            get { lock (_sync) return _started; }
            set { lock (_sync) _started = value; }
        }
        internal Func<RequestedCameraConfiguration, Task<CameraConfigurationResult>>?
            ApplyHandler { get; set; }
        internal Func<CancellationToken, Task<CameraOperationResult>>? StartHandler { get; set; }
        internal TaskCompletionSource<bool> ApplyStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ApplyCalls => Volatile.Read(ref _applyCalls);
        internal int StartCalls => Volatile.Read(ref _startCalls);
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal int AcquireCalls => Volatile.Read(ref _acquireCalls);
        internal int HealthCalls => Volatile.Read(ref _healthCalls);
        internal EffectiveCameraConfiguration CurrentEffective
        {
            get { lock (_sync) return _effective; }
        }

        public CameraHealthSnapshot GetHealthSnapshot()
        {
            Interlocked.Increment(ref _healthCalls);
            lock (_sync)
            {
                if (_disposed)
                    return new(CameraProviderAvailability.Available,
                        CameraConnectionState.Closed, CameraConfigurationState.Unconfigured,
                        CameraAcquisitionState.Stopped, _clock.GetTimePoint());
                return new(CameraProviderAvailability.Available, CameraConnectionState.Open,
                    CameraConfigurationState.Applied,
                    _started ? CameraAcquisitionState.Armed : CameraAcquisitionState.Stopped,
                    _clock.GetTimePoint());
            }
        }

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            FrameAcquisitionControl control, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _acquireCalls);
            control.AcknowledgePending(request);
            return new ValueTask<FrameAcquisitionResult>(AcquireCoreAsync(request, control,
                cancellationToken));
        }

        private async Task<FrameAcquisitionResult> AcquireCoreAsync(
            FrameAcquisitionRequest request, FrameAcquisitionControl control,
            CancellationToken cancellationToken)
        {
            var start = await control.WaitForBusyAsync(cancellationToken).ConfigureAwait(false);
            var ready = start.BusyAt.MonotonicTimestamp + 1;
            return FrameResult(request, start, ready);
        }

        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _applyCalls);
            ApplyStarted.TrySetResult(true);
            if (ApplyHandler is { } handler)
                return new ValueTask<CameraConfigurationResult>(ApplyAndRememberAsync(
                    requested, handler));
            return ValueTask.FromResult(ApplyAndRemember(requested));
        }

        private async Task<CameraConfigurationResult> ApplyAndRememberAsync(
            RequestedCameraConfiguration requested,
            Func<RequestedCameraConfiguration, Task<CameraConfigurationResult>> handler)
        {
            var result = await handler(requested).ConfigureAwait(false);
            if (result.Succeeded && result.Effective is not null)
                lock (_sync) _effective = result.Effective;
            return result;
        }

        private CameraConfigurationResult ApplyAndRemember(
            RequestedCameraConfiguration requested)
        {
            var result = Capabilities.ValidateConfiguration(requested);
            if (result.Succeeded && result.Effective is not null)
                lock (_sync) _effective = result.Effective;
            return result;
        }

        public ValueTask<CameraOperationResult> StartAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCalls);
            if (StartHandler is { } handler)
                return new ValueTask<CameraOperationResult>(StartAndRememberAsync(handler,
                    cancellationToken));
            lock (_sync) _started = true;
            return ValueTask.FromResult(CameraOperationResult.Success("FixtureStarted"));
        }

        private async Task<CameraOperationResult> StartAndRememberAsync(
            Func<CancellationToken, Task<CameraOperationResult>> handler,
            CancellationToken cancellationToken)
        {
            var result = await handler(cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
                lock (_sync) _started = true;
            return result;
        }

        public ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stopCalls);
            lock (_sync) _started = false;
            return ValueTask.FromResult(CameraOperationResult.Success("FixtureStopped"));
        }

        public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence,
            int maximumCount = 64) =>
            new(_protocolEpoch, 1, 0, false, Array.Empty<CameraProtocolObservation>());

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            lock (_sync)
            {
                _started = false;
                _disposed = true;
            }
            return ValueTask.CompletedTask;
        }

        private FrameAcquisitionResult FrameResult(FrameAcquisitionRequest request,
            FrameAcquisitionStart start, long ready)
        {
            var effective = _effective;
            var metadata = new FrameMetadata(request.Correlation, request.LogicalCameraRole,
                effective.RegionOfInterest.Width, effective.RegionOfInterest.Height, 1,
                effective.PixelFormat, effective.ValidBits, _clock.GetTimePoint().HostObservedAtUtc,
                effective);
            var point = new FrameTimePoint(_clock.GetTimePoint().HostObservedAtUtc, ready);
            var milestones = new FrameAcquisitionMilestones(_clock.Frequency, point, point,
                point, point);
            var provenance = new FrameProvenance(request.Correlation,
                Descriptor.Provider.Id, Descriptor.Provider.Version,
                Descriptor.Provider.AdapterPackageId, Descriptor.Provider.AdapterVersion,
                "FixtureSdk", "1", null, Descriptor.StableDeviceIdentity,
                Descriptor.ReportedModel, null, "Mono8", "fixture-v1", false, false,
                null, 1, milestones);
            return FrameAcquisitionResult.Success(new TestLease(
                new TestFrame(metadata), provenance));
        }

        private static CameraCapabilities FixtureCapabilities() => new(
            new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new CameraDoubleCapability(1, 100, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            new CameraRoiCapabilities(1, 1, new(0, 0, 1), new(0, 0, 1),
                new(1, 1, 1), new(1, 1, 1)));
    }

    private sealed class TestLease : IFrameBufferLease
    {
        private int _returned;

        internal TestLease(VisionFrame frame, FrameProvenance provenance)
        {
            Frame = frame;
            Provenance = provenance;
            LeaseId = Guid.NewGuid();
        }

        public Guid LeaseId { get; }
        public VisionFrame Frame { get; }
        public FrameProvenance Provenance { get; }
        public bool IsReturned => Volatile.Read(ref _returned) != 0;
        public void Dispose() => Interlocked.Exchange(ref _returned, 1);
    }

    private sealed class TestFrame : VisionFrame
    {
        internal TestFrame(FrameMetadata metadata) : base(metadata) { }
        public override bool IsLoanActive => true;
        public override ReadOnlySpan<byte> GetRowSpan(int row) => new byte[] { 42 };
    }

    private sealed class ManualClock : IFrameAcquisitionClock
    {
        private readonly object _sync = new();
        private readonly List<Scheduled> _scheduled = new();
        private long _timestamp = 100;
        public long Frequency => 1_000;

        public FrameTimePoint GetTimePoint()
        {
            lock (_sync)
                return new FrameTimePoint(DateTimeOffset.UnixEpoch.AddSeconds(_timestamp),
                    _timestamp);
        }

        public IDisposable Schedule(long dueTimestamp, FrameAcquisitionClockPhase phase,
            Action callback)
        {
            var item = new Scheduled(dueTimestamp, callback);
            lock (_sync) _scheduled.Add(item);
            return item;
        }

        private sealed class Scheduled : IDisposable
        {
            internal Scheduled(long dueTimestamp, Action callback)
            {
                DueTimestamp = dueTimestamp;
                Callback = callback;
            }
            internal long DueTimestamp { get; }
            internal Action Callback { get; }
            public void Dispose() { }
        }
    }
}
