using System.Collections.Concurrent;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CameraRecoveryServiceTests
{
    [ThreadStatic]
    private static bool s_providerCompletionStack;

    [Fact]
    public void V119_R01_DefaultsAreBoundedAndProductionIsNeverAccepted()
    {
        var options = new CameraRecoveryOptions();
        Assert.Equal(TimeSpan.FromSeconds(5), options.RetryInterval);
        Assert.Equal(20, options.MaximumAttempts);
        Assert.Equal(TimeSpan.FromSeconds(5), options.OperationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ShutdownWaitTimeout);
    }

    [Fact]
    public async Task V119_R02_ConstructorRequiresExactProviderAndStableDeviceIdentity()
    {
        var fixture = RecoveryFixture.Create();
        await using var service = fixture.Service;

        var wrongProvider = new RecoveryProvider(new CameraProviderIdentity(
            "FixtureProvider", "2", "FixtureAdapter", "1"));
        Assert.Throws<ArgumentException>(() => new CameraRecoveryService(wrongProvider,
            fixture.Target, "Primary", Requested(),
            fixture.InitialAcquisition,
            fixture.Clock, new CameraRecoveryOptions()));

        var wrongTarget = new CameraBindingTarget(fixture.Target.Provider, "OtherDevice");
        Assert.Throws<ArgumentException>(() => new CameraRecoveryService(fixture.Provider,
            wrongTarget, "Primary", Requested(),
            fixture.InitialAcquisition,
            fixture.Clock, new CameraRecoveryOptions()));
    }

    [Fact]
    public async Task V119_R03_HealthySeedDelegatesQualificationAndRejectsProductionWithoutId()
    {
        var fixture = RecoveryFixture.Create();
        await using var service = fixture.Service;

        var initialRevision = service.GetSnapshot().HealthObservationRevision;
        await service.RefreshAsync();
        var healthySnapshot = service.GetSnapshot();
        Assert.Equal(CameraRecoveryState.Healthy, healthySnapshot.State);
        Assert.True(healthySnapshot.HealthObservationRevision > initialRevision);

        var production = await service.AcquireAsync(ExecutionKind.Production, "Primary");
        Assert.False(production.Accepted);
        Assert.Null(production.Correlation);
        Assert.Equal("ProductionAcquisitionUnavailable", production.ReasonCode);

        var qualification = await service.AcquireAsync(ExecutionKind.Qualification, "Primary");
        Assert.True(qualification.Accepted);
        Assert.NotNull(qualification.Correlation);
        qualification.Outcome?.Dispose();
    }

    [Fact]
    public async Task V119_R04_RecoveryUsesOnlyExactStableBindingAndIgnoresReportedModel()
    {
        var fixture = RecoveryFixture.Create();
        var candidate = new RecoveryDevice(fixture.Target, fixture.Clock)
        {
            ReportedModel = "A-different-model"
        };
        fixture.Provider.OpenHandler = (_, _) => Task.FromResult(CameraOpenResult.Success(candidate));
        await using var service = fixture.Service;

        fixture.Initial.HealthHandler = () => Disconnected(fixture.Clock);
        await service.RefreshAsync();
        fixture.Clock.FireDue();
        await EventuallyAsync(() => service.GetSnapshot().State == CameraRecoveryState.Healthy);

        Assert.Equal(new[] { fixture.Target.StableDeviceIdentity },
            fixture.Provider.OpenedIdentities.ToArray());
        Assert.Equal(1, candidate.ApplyCalls);
        Assert.Equal(1, candidate.StartCalls);
        Assert.Equal(CameraRecoveryState.Healthy, service.GetSnapshot().State);
    }

    [Fact]
    public async Task V119_R05_ReadBackFailureStopsAndDisposesCandidateBeforeExhaustion()
    {
        var fixture = RecoveryFixture.Create(maximumAttempts: 1);
        var candidate = new RecoveryDevice(fixture.Target, fixture.Clock)
        {
            ApplyHandler = _ => CameraConfigurationResult.Success(
                new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
                    11, 0, Requested().RegionOfInterest, VisionPixelFormat.Mono8, null, 100, 0,
                    null))
        };
        fixture.Provider.OpenHandler = (_, _) => Task.FromResult(CameraOpenResult.Success(candidate));
        await using var service = fixture.Service;

        fixture.Initial.HealthHandler = () => Disconnected(fixture.Clock);
        await service.RefreshAsync();
        fixture.Clock.FireDue();
        await EventuallyAsync(() => service.GetSnapshot().State == CameraRecoveryState.Exhausted);

        Assert.Equal(1, candidate.ApplyCalls);
        Assert.Equal(0, candidate.StartCalls);
        Assert.Equal(1, candidate.StopCalls);
        Assert.Equal(1, candidate.DisposeCalls);
        Assert.Contains(service.ReadEvents(0).Events,
            item => item.Kind == CameraRecoveryEventKind.AttemptFailed &&
                    item.ReasonCode == "CameraRecoveryReadBackFailed");
    }

    [Fact]
    public async Task V119_R06_AttemptsUseMonotonicIntervalAndExhaustionDoesNotAutoRestart()
    {
        var fixture = RecoveryFixture.Create(maximumAttempts: 2,
            retryInterval: TimeSpan.FromSeconds(1));
        fixture.Provider.OpenHandler = (_, _) =>
            Task.FromResult(CameraOpenResult.Failure("FixtureOpenFailed"));
        await using var service = fixture.Service;

        fixture.Initial.HealthHandler = () => Disconnected(fixture.Clock);
        await service.RefreshAsync();
        fixture.Clock.FireDue();
        await EventuallyAsync(() => fixture.Provider.OpenCalls == 1);

        var next = service.GetSnapshot().NextAttemptTimestamp;
        Assert.True(next.HasValue);
        fixture.Clock.AdvanceTo(next!.Value - 1);
        await Task.Delay(30);
        Assert.Equal(1, fixture.Provider.OpenCalls);

        fixture.Clock.UtcNow = DateTimeOffset.UtcNow.AddYears(5);
        fixture.Clock.AdvanceTo(next.Value);
        await EventuallyAsync(() => fixture.Provider.OpenCalls == 2);
        await EventuallyAsync(() => service.GetSnapshot().State == CameraRecoveryState.Exhausted);

        var exhausted = service.GetSnapshot();
        fixture.Initial.HealthHandler = () => Healthy(fixture.Clock);
        await service.RefreshAsync();
        fixture.Clock.AdvanceTo(next.Value + 100_000);
        await Task.Delay(30);
        Assert.Equal(2, fixture.Provider.OpenCalls);
        Assert.Equal(CameraRecoveryState.Exhausted, service.GetSnapshot().State);
        Assert.Equal(exhausted.CycleId, service.GetSnapshot().CycleId);
    }

    [Fact]
    public async Task V119_R07_OnlyAnInternalReservationCanStartASecondCycle()
    {
        var fixture = RecoveryFixture.Create(maximumAttempts: 1,
            retryInterval: TimeSpan.FromMilliseconds(10));
        fixture.Provider.OpenHandler = (_, _) =>
            Task.FromResult(CameraOpenResult.Failure("FixtureOpenFailed"));
        await using var service = fixture.Service;

        fixture.Initial.HealthHandler = () => Disconnected(fixture.Clock);
        await service.RefreshAsync();
        fixture.Clock.FireDue();
        await EventuallyAsync(() => service.GetSnapshot().State == CameraRecoveryState.Exhausted);
        var cycleId = service.GetSnapshot().CycleId!.Value;

        var reservation = service.TryReserveRestart(cycleId, out var reservedReason);
        Assert.NotNull(reservation);
        Assert.Equal("CameraRecoveryRestartReserved", reservedReason);
        Assert.Null(service.TryReserveRestart(cycleId, out var duplicateReason));
        Assert.Equal("CameraRecoveryRestartAlreadyReserved", duplicateReason);

        Assert.True(reservation!.Commit(out var commitReason));
        Assert.Equal("CameraRecoveryRestartCommitted", commitReason);
        fixture.Clock.FireDue();
        await EventuallyAsync(() => fixture.Provider.OpenCalls == 2);
    }

    [Fact]
    public async Task V119_R08_LateOpenRemainsOwnedUntilDisposeAndDoesNotOverlapAnotherOpen()
    {
        var fixture = RecoveryFixture.Create(maximumAttempts: 1,
            operationTimeout: TimeSpan.FromMilliseconds(25),
            shutdownWaitTimeout: TimeSpan.FromMilliseconds(25));
        var openRelease = new TaskCompletionSource<CameraOpenResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateDevice = new RecoveryDevice(fixture.Target, fixture.Clock);
        fixture.Provider.OpenHandler = (_, _) => openRelease.Task;
        var service = fixture.Service;

        fixture.Initial.HealthHandler = () => Disconnected(fixture.Clock);
        await service.RefreshAsync();
        fixture.Clock.FireDue();
        await EventuallyAsync(() => fixture.Provider.OpenCalls == 1);
        await EventuallyAsync(() => service.GetSnapshot().State == CameraRecoveryState.RecoveryRequired);

        await service.DisposeAsync();
        Assert.Equal(0, fixture.Provider.DisposeCalls);

        openRelease.SetResult(CameraOpenResult.Success(lateDevice));
        await EventuallyAsync(() => fixture.Provider.DisposeCalls == 1);
        Assert.Equal(1, lateDevice.StopCalls);
        Assert.Equal(1, lateDevice.DisposeCalls);
    }

    [Fact]
    public async Task V119_R09_QuantizedPreparedSeedMustMatchCapabilityEffectiveConfiguration()
    {
        var clock = new RecoveryClock();
        var identity = new CameraProviderIdentity("FixtureProvider", "1",
            "FixtureAdapter", "1");
        var target = new CameraBindingTarget(identity, "FixtureDevice");
        var provider = new RecoveryProvider(identity);
        var capabilities = QuantizedCapabilities();
        var initial = new RecoveryDevice(target, clock, capabilities);
        var requested = new RequestedCameraConfiguration(
            ProductionAcquisitionMode.SoftwareTrigger, 10.4, 0,
            new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8, null, 100, 0,
            null);
        var expected = capabilities.ValidateConfiguration(requested);
        Assert.True(expected.Succeeded);
        Assert.NotNull(expected.Effective);
        Assert.Equal(10, expected.Effective!.ExposureTimeUs);

        var acquisition = new CameraAcquisitionService(initial, expected.Effective,
            clock, new CameraAcquisitionOptions(TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1), protocolReadTimeout: TimeSpan.FromSeconds(1)));
        await using var service = new CameraRecoveryService(provider, target, "Primary",
            requested, acquisition, clock, new CameraRecoveryOptions());

        await service.RefreshAsync();
        Assert.Equal(CameraRecoveryState.Healthy, service.GetSnapshot().State);
        Assert.Equal(expected.Effective, acquisition.Configuration);
    }

    [Fact]
    public async Task V119_R10_BufferAndProtocolFaultsDoNotStartAutomaticReconnect()
    {
        var bufferFixture = RecoveryFixture.Create();
        await using var bufferService = bufferFixture.Service;
        bufferFixture.Initial.HealthHandler = () => FaultedHealthy(bufferFixture.Clock,
            CameraFaultClassification.BufferFault, "FixtureBufferFault");

        await bufferService.RefreshAsync();
        var bufferSnapshot = bufferService.GetSnapshot();
        Assert.Equal(CameraRecoveryState.RecoveryRequired, bufferSnapshot.State);
        Assert.False(bufferSnapshot.SourceHealthy);
        Assert.Equal(0, bufferFixture.Provider.OpenCalls);

        var protocolFixture = RecoveryFixture.Create();
        await using var protocolService = protocolFixture.Service;
        protocolFixture.Initial.ProtocolGap = true;

        await protocolService.RefreshAsync();
        var protocolSnapshot = protocolService.GetSnapshot();
        Assert.Equal(CameraRecoveryState.RecoveryRequired, protocolSnapshot.State);
        Assert.False(protocolSnapshot.SourceHealthy);
        Assert.Equal(0, protocolFixture.Provider.OpenCalls);
        Assert.Contains(protocolService.ReadProtocolObservations(0).Observations,
            item => item.Kind == CameraProtocolViolationKind.ObservationGap);
    }

    [Fact]
    public async Task V119_R11_ProtocolFactsSurviveExplicitChildEpochSwitch()
    {
        var fixture = RecoveryFixture.Create();
        fixture.Initial.AddProtocolObservation(CameraProtocolViolationKind.InvalidFrame,
            "CameraInvalidFrame");
        var candidate = new RecoveryDevice(fixture.Target, fixture.Clock);
        candidate.AddProtocolObservation(CameraProtocolViolationKind.LateFrame,
            "CameraLateFrame");
        fixture.Provider.OpenHandler = (_, _) => Task.FromResult(CameraOpenResult.Success(candidate));
        await using var service = fixture.Service;

        await service.RefreshAsync();
        Assert.Equal(CameraRecoveryState.Healthy, service.GetSnapshot().State);
        fixture.Initial.HealthHandler = () => Disconnected(fixture.Clock);
        await service.RefreshAsync();
        fixture.Clock.FireDue();
        await EventuallyAsync(() => service.GetSnapshot().State == CameraRecoveryState.Healthy);

        var observations = service.ReadProtocolObservations(0).Observations;
        Assert.Equal(new[] { CameraProtocolViolationKind.InvalidFrame,
            CameraProtocolViolationKind.LateFrame }, observations.Select(item => item.Kind));
        Assert.Equal(new[] { "CameraInvalidFrame", "CameraLateFrame" },
            observations.Select(item => item.ReasonCode));
        Assert.Equal(new[] { 1L, 2L }, observations.Select(item => item.Sequence));
        Assert.False(service.GetSnapshot().State == CameraRecoveryState.RecoveryRequired);
    }

    [Fact]
    public async Task V119_R12_HealthRevisionAdvancesOnlyAfterActualProbeCompletes()
    {
        var fixture = RecoveryFixture.Create();
        await using var service = fixture.Service;
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Initial.HealthHandler = () =>
        {
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
            return Healthy(fixture.Clock);
        };

        var first = service.RefreshAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var beforeBusy = service.GetSnapshot().HealthObservationRevision;
        await service.RefreshAsync();
        Assert.Equal(beforeBusy, service.GetSnapshot().HealthObservationRevision);

        release.SetResult(true);
        await first;
        Assert.Equal(beforeBusy + 1, service.GetSnapshot().HealthObservationRevision);
    }

    [Fact]
    public async Task V119_R13_LateSafeRetirementResumesTheRemainingBoundedCycle()
    {
        var fixture = RecoveryFixture.Create(maximumAttempts: 2,
            retryInterval: TimeSpan.FromMilliseconds(10),
            operationTimeout: TimeSpan.FromMilliseconds(25));
        var retirementRelease = new TaskCompletionSource<CameraOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Initial.StopHandler = _ => retirementRelease.Task;
        var candidate = new RecoveryDevice(fixture.Target, fixture.Clock);
        fixture.Provider.OpenHandler = (_, _) =>
            Task.FromResult(CameraOpenResult.Success(candidate));
        await using var service = fixture.Service;

        fixture.Initial.HealthHandler = () => Disconnected(fixture.Clock);
        await service.RefreshAsync();
        fixture.Clock.FireDue();
        await EventuallyAsync(() => service.GetSnapshot().State ==
            CameraRecoveryState.RecoveryRequired);
        Assert.Equal(0, fixture.Provider.OpenCalls);

        retirementRelease.SetResult(CameraOperationResult.Success("FixtureStopped"));
        await EventuallyAsync(() => service.GetSnapshot().State ==
            CameraRecoveryState.Recovering);
        var next = service.GetSnapshot().NextAttemptTimestamp;
        Assert.True(next.HasValue);
        fixture.Clock.AdvanceTo(next!.Value);
        await EventuallyAsync(() => service.GetSnapshot().State ==
            CameraRecoveryState.Healthy);
        Assert.Equal(1, fixture.Provider.OpenCalls);
    }

    [Fact]
    public async Task V119_R14_ProviderCompletionNeverRunsRecoverySchedulingInline()
    {
        {
            var fixture = RecoveryFixture.Create(maximumAttempts: 2,
                retryInterval: TimeSpan.FromMilliseconds(10),
                operationTimeout: TimeSpan.FromMilliseconds(25));
            fixture.Clock.CompletionStackProbe = () => s_providerCompletionStack;
            var openRelease = new TaskCompletionSource<CameraOpenResult>();
            var candidate = new RecoveryDevice(fixture.Target, fixture.Clock);
            fixture.Provider.OpenHandler = (_, _) => openRelease.Task;
            await using var service = fixture.Service;

            fixture.Initial.HealthHandler = () => Disconnected(fixture.Clock);
            await service.RefreshAsync();
            fixture.Clock.FireDue();
            await EventuallyAsync(() => service.GetSnapshot().State ==
                CameraRecoveryState.RecoveryRequired);

            s_providerCompletionStack = true;
            try { openRelease.SetResult(CameraOpenResult.Success(candidate)); }
            finally { s_providerCompletionStack = false; }

            Assert.False(fixture.Clock.InlineScheduleObserved);
            await EventuallyAsync(() => service.GetSnapshot().State ==
                CameraRecoveryState.Recovering);
        }

        {
            var fixture = RecoveryFixture.Create(maximumAttempts: 2,
                retryInterval: TimeSpan.FromMilliseconds(10),
                operationTimeout: TimeSpan.FromMilliseconds(25));
            fixture.Clock.CompletionStackProbe = () => s_providerCompletionStack;
            var applyRelease = new TaskCompletionSource<CameraConfigurationResult>();
            var candidate = new RecoveryDevice(fixture.Target, fixture.Clock)
            {
                ApplyAsyncHandler = _ => applyRelease.Task
            };
            fixture.Provider.OpenHandler = (_, _) =>
                Task.FromResult(CameraOpenResult.Success(candidate));
            await using var service = fixture.Service;

            fixture.Initial.HealthHandler = () => Disconnected(fixture.Clock);
            await service.RefreshAsync();
            fixture.Clock.FireDue();
            await EventuallyAsync(() => candidate.ApplyCalls == 1 &&
                service.GetSnapshot().State == CameraRecoveryState.RecoveryRequired);

            s_providerCompletionStack = true;
            try { applyRelease.SetResult(CameraConfigurationResult.Success(Effective())); }
            finally { s_providerCompletionStack = false; }

            Assert.False(fixture.Clock.InlineScheduleObserved);
            await EventuallyAsync(() => service.GetSnapshot().State ==
                CameraRecoveryState.Recovering);
        }
    }

    private static RequestedCameraConfiguration Requested() => new(
        ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
        new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8, null, 100, 0, null);

    private static EffectiveCameraConfiguration Effective() => new(
        ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
        new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8, null, 100, 0, null);

    private static CameraHealthSnapshot Healthy(RecoveryClock clock) => new(
        CameraProviderAvailability.Available, CameraConnectionState.Open,
        CameraConfigurationState.Applied, CameraAcquisitionState.Armed,
        clock.GetTimePoint());

    private static CameraHealthSnapshot Disconnected(RecoveryClock clock) => new(
        CameraProviderAvailability.Available, CameraConnectionState.Disconnected,
        CameraConfigurationState.Unknown, CameraAcquisitionState.Stopped,
        clock.GetTimePoint(), new CameraFault(CameraFaultClassification.ConnectionLost,
            "FixtureConnectionLost"));

    private static CameraHealthSnapshot FaultedHealthy(RecoveryClock clock,
        CameraFaultClassification classification, string reason) => new(
        CameraProviderAvailability.Available, CameraConnectionState.Open,
        CameraConfigurationState.Applied, CameraAcquisitionState.Armed,
        clock.GetTimePoint(), new CameraFault(classification, reason));

    private static CameraCapabilities QuantizedCapabilities() => new(
        new[] { ProductionAcquisitionMode.SoftwareTrigger },
        new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
        new CameraDoubleCapability(1, 100, 1, CameraQuantizationMode.Nearest, 1),
        new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
        new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
        new CameraRoiCapabilities(1, 1, new(0, 0, 1), new(0, 0, 1),
            new(1, 1, 1), new(1, 1, 1)));

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("FixtureEventuallyTimeout");
            await Task.Delay(5);
        }
    }

    private sealed class RecoveryFixture
    {
        private RecoveryFixture(RecoveryProvider provider, CameraBindingTarget target,
            RecoveryClock clock, RecoveryDevice initial,
            CameraAcquisitionService initialAcquisition, CameraRecoveryService service)
        {
            Provider = provider;
            Target = target;
            Clock = clock;
            Initial = initial;
            InitialAcquisition = initialAcquisition;
            Service = service;
        }

        internal RecoveryProvider Provider { get; }
        internal CameraBindingTarget Target { get; }
        internal RecoveryClock Clock { get; }
        internal RecoveryDevice Initial { get; }
        internal CameraAcquisitionService InitialAcquisition { get; }
        internal CameraRecoveryService Service { get; }

        internal static RecoveryFixture Create(int maximumAttempts = 20,
            TimeSpan? retryInterval = null, TimeSpan? operationTimeout = null,
            TimeSpan? shutdownWaitTimeout = null)
        {
            var clock = new RecoveryClock();
            var identity = new CameraProviderIdentity("FixtureProvider", "1",
                "FixtureAdapter", "1");
            var target = new CameraBindingTarget(identity, "FixtureDevice");
            var provider = new RecoveryProvider(identity);
            var initial = new RecoveryDevice(target, clock);
            var acquisition = new CameraAcquisitionService(initial, Effective(), clock,
                new CameraAcquisitionOptions(TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1), protocolReadTimeout: TimeSpan.FromSeconds(1)));
            var service = new CameraRecoveryService(provider, target, "Primary", Requested(),
                acquisition, clock, new CameraRecoveryOptions(
                    retryInterval ?? TimeSpan.FromSeconds(5), maximumAttempts,
                    operationTimeout ?? TimeSpan.FromSeconds(1),
                    shutdownWaitTimeout ?? TimeSpan.FromSeconds(1)));
            return new(provider, target, clock, initial, acquisition, service);
        }
    }

    private sealed class RecoveryProvider : ICameraProvider
    {
        private int _openCalls;
        private int _disposeCalls;

        internal RecoveryProvider(CameraProviderIdentity identity) => Identity = identity;

        public CameraProviderIdentity Identity { get; }
        internal Func<string, CancellationToken, Task<CameraOpenResult>>? OpenHandler { get; set; }
        internal ConcurrentQueue<string> OpenedIdentities { get; } = new();
        internal int OpenCalls => Volatile.Read(ref _openCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("RecoveryMustNotDiscoverReplacement");

        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openCalls);
            OpenedIdentities.Enqueue(stableDeviceIdentity);
            var task = OpenHandler?.Invoke(stableDeviceIdentity, cancellationToken) ??
                Task.FromResult(CameraOpenResult.Failure("FixtureOpenHandlerMissing"));
            return new ValueTask<CameraOpenResult>(task);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecoveryDevice : IControlledCameraDevice
    {
        private readonly Guid _protocolEpoch = Guid.NewGuid();
        private readonly RecoveryClock _clock;
        private int _applyCalls;
        private int _startCalls;
        private int _stopCalls;
        private int _disposeCalls;

        internal RecoveryDevice(CameraBindingTarget target, RecoveryClock clock,
            CameraCapabilities? capabilities = null)
        {
            _clock = clock;
            Descriptor = new CameraDeviceDescriptor(target.Provider,
                target.StableDeviceIdentity, "Fixture camera", "Fixture model");
            HealthHandler = () => Healthy(_clock);
            Capabilities = capabilities ?? DefaultCapabilities();
        }

        internal string? ReportedModel
        {
            set => Descriptor = new CameraDeviceDescriptor(Descriptor.Provider,
                Descriptor.StableDeviceIdentity, Descriptor.DisplayName, value);
        }

        internal Func<CameraHealthSnapshot> HealthHandler { get; set; }
        internal Func<RequestedCameraConfiguration, CameraConfigurationResult> ApplyHandler { get; set; }
            = requested => CameraConfigurationResult.Success(new EffectiveCameraConfiguration(
                requested.ProductionAcquisitionMode, requested.ExposureTimeUs, requested.GainDb,
                requested.RegionOfInterest, requested.PixelFormat, requested.ValidBits,
                requested.AcquisitionTimeoutMs, requested.TriggerDelayUs,
                requested.WhiteBalanceRgb));
        internal Func<RequestedCameraConfiguration, Task<CameraConfigurationResult>>?
            ApplyAsyncHandler { get; set; }
        internal Func<CancellationToken, Task<CameraOperationResult>> StopHandler { get; set; }
            = _ => Task.FromResult(CameraOperationResult.Success("FixtureStopped"));

        public CameraDeviceDescriptor Descriptor { get; private set; }
        public CameraCapabilities Capabilities { get; }
        internal bool ProtocolGap { get; set; }
        private readonly List<CameraProtocolObservation> _protocolObservations = new();

        internal void AddProtocolObservation(CameraProtocolViolationKind kind, string reason)
        {
            _protocolObservations.Add(new CameraProtocolObservation(
                _protocolObservations.Count + 1L, kind, reason, _clock.GetTimePoint()));
        }

        internal int ApplyCalls => Volatile.Read(ref _applyCalls);
        internal int StartCalls => Volatile.Read(ref _startCalls);
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public CameraHealthSnapshot GetHealthSnapshot() => HealthHandler();

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(FrameAcquisitionResult.FailureResult(new CameraAcquisitionFailure(
                CameraAcquisitionFailureKind.DeviceFault, "FixtureAcquireUnavailable")));

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            FrameAcquisitionControl control, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(FrameAcquisitionResult.FailureResult(new CameraAcquisitionFailure(
                CameraAcquisitionFailureKind.DeviceFault, "FixtureAcquireUnavailable")));

        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _applyCalls);
            if (ApplyAsyncHandler is { } applyAsync)
                return new ValueTask<CameraConfigurationResult>(applyAsync(requested));
            return ValueTask.FromResult(ApplyHandler(requested));
        }

        public ValueTask<CameraOperationResult> StartAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCalls);
            return ValueTask.FromResult(CameraOperationResult.Success("FixtureStarted"));
        }

        public ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stopCalls);
            return new ValueTask<CameraOperationResult>(StopHandler(cancellationToken));
        }

        public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence,
            int maximumCount = 64)
        {
            if (ProtocolGap)
                return new(_protocolEpoch, 2, 2, false,
                    Array.Empty<CameraProtocolObservation>());
            var through = _protocolObservations.Count;
            var observations = _protocolObservations
                .Where(item => item.Sequence > afterSequence)
                .Take(maximumCount)
                .ToArray();
            var first = _protocolObservations.Count == 0 ? 1 :
                _protocolObservations[0].Sequence;
            return new(_protocolEpoch, first, through, false, observations);
        }

        private static CameraCapabilities DefaultCapabilities() => new(
            new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new CameraDoubleCapability(1, 100, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            new CameraRoiCapabilities(1, 1, new(0, 0, 1), new(0, 0, 1),
                new(1, 1, 1), new(1, 1, 1)));

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecoveryClock : IFrameAcquisitionClock
    {
        private readonly object _sync = new();
        private readonly List<Scheduled> _scheduled = new();
        private long _timestamp;

        internal DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
        internal Func<bool>? CompletionStackProbe { get; set; }
        internal bool InlineScheduleObserved { get; private set; }
        public long Frequency => 1_000;

        public FrameTimePoint GetTimePoint()
        {
            lock (_sync) return new FrameTimePoint(UtcNow, _timestamp);
        }

        public IDisposable Schedule(long dueTimestamp, FrameAcquisitionClockPhase phase,
            Action callback)
        {
            if (CompletionStackProbe?.Invoke() == true)
                InlineScheduleObserved = true;
            var scheduled = new Scheduled(dueTimestamp, callback);
            lock (_sync) _scheduled.Add(scheduled);
            return scheduled;
        }

        internal void FireDue() => AdvanceTo(_timestamp);

        internal void AdvanceTo(long timestamp)
        {
            Scheduled[] due;
            lock (_sync)
            {
                _timestamp = timestamp;
                due = _scheduled.Where(item => !item.IsDisposed && !item.Fired &&
                        item.DueTimestamp <= timestamp).ToArray();
                foreach (var item in due) item.Fired = true;
            }

            foreach (var item in due)
                _ = Task.Run(item.Callback);
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
            internal bool Fired { get; set; }
            internal bool IsDisposed { get; private set; }
            public void Dispose() => IsDisposed = true;
        }
    }
}
