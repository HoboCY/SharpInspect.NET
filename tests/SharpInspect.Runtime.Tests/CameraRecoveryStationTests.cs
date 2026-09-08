using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CameraRecoveryStationTests
{
    [Fact]
    public async Task V119_I01_MissingRecoveryMappingKeepsAlarmAuthorityAndReadyClosed()
    {
        await using var fixture = await Fixture.CreateAsync(includeRecoveryMapping: false);
        var snapshot = await fixture.Runtime.GetSnapshotAsync();
        Assert.False(snapshot.Ready);
        Assert.Equal(ProductionArmState.Disarmed, snapshot.ArmState);
        Assert.Contains("CameraRecoveryAlarmMappingUnavailable", snapshot.AdmissionBlockers);
        Assert.False(snapshot.AlarmState!.Available);
        Assert.NotNull(snapshot.CameraRecovery);
    }

    [Fact]
    public async Task V119_I02_SourceRecoveryKeepsFailedLatchAndUnconfirmedResultInterlocks()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Until(() => fixture.Engine.GetSnapshot().SourceHealthy);
        fixture.Provider.OpenFailures = 1;
        fixture.Initial.Disconnect();
        await Until(() => fixture.Engine.GetSnapshot().NextAttemptTimestamp.HasValue);
        fixture.Clock.AdvanceTo(fixture.Engine.GetSnapshot().NextAttemptTimestamp!.Value);
        await Until(() => fixture.Engine.GetSnapshot().State == CameraRecoveryState.Exhausted);
        await Until(async () => (await fixture.Runtime.GetSnapshotAsync()).AlarmState!.Instances.Any(item =>
            item.Code == "CameraRecoveryFailed" && item.Lifecycle != AlarmLifecycle.Cleared));
        var exhausted = fixture.Engine.GetSnapshot();
        var before = await fixture.Runtime.GetSnapshotAsync();
        var failed = before.AlarmState!.Instances.Single(item => item.Code == "CameraRecoveryFailed");
        Assert.True(failed.IsLatched);

        // Test-only setup of an unresolved result. Production execution and PLC
        // acceptance are deliberately unavailable in this development component.
        var field = typeof(StationRuntime).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sync = typeof(StationRuntime).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Runtime)!;
        lock (sync)
        {
            var current = (StationStateSnapshot)field.GetValue(fixture.Runtime)!;
            field.SetValue(fixture.Runtime, current with { Handshake = HandshakePhase.AwaitingResultAck,
                Recovery = RecoveryState.Required, Evidence = current.Evidence with { PendingDeliveries = 1 } });
        }
        // Exercise the source transition independently of the human authorization
        // boundary, which is covered by the real command/consumer tests.
        using var reservation = fixture.Engine.TryReserveRestart(exhausted.CycleId!.Value, out var reserveReason);
        Assert.NotNull(reservation);
        Assert.True(reservation!.Commit(out var startReason), startReason ?? reserveReason);
        await Until(() => fixture.Engine.GetSnapshot().NextAttemptTimestamp.HasValue);
        fixture.Clock.AdvanceTo(fixture.Engine.GetSnapshot().NextAttemptTimestamp!.Value);
        await Until(() => fixture.Engine.GetSnapshot().SourceHealthy);
        await Until(async () => (await fixture.Runtime.GetSnapshotAsync()).CameraRecovery?.SourceHealthy == true);

        var after = await fixture.Runtime.GetSnapshotAsync();
        Assert.False(after.Ready);
        Assert.Equal(ProductionArmState.Disarmed, after.ArmState);
        Assert.Equal(HandshakePhase.AwaitingResultAck, after.Handshake);
        Assert.Equal(RecoveryState.Required, after.Recovery);
        Assert.Equal(1, after.Evidence.PendingDeliveries);
        var retained = after.AlarmState!.Instances.Single(item => item.InstanceId == failed.InstanceId);
        Assert.True(retained.IsLatched);
        Assert.False(retained.Acknowledged);
        Assert.NotEqual(AlarmLifecycle.Cleared, retained.Lifecycle);
        Assert.Equal(new[] { "Camera.One", "Camera.One" }, fixture.Provider.OpenedIdentities);
        Assert.Equal(1, fixture.Provider.LastOpened!.ApplyCount);
        Assert.Equal(1, fixture.Provider.LastOpened.StartCount);
    }

    [Fact]
    public async Task V119_I03_RecoveryOwnerRejectsSecondAcquisitionRegistrationAndSetupMutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var services = new ServiceCollection();
        services.AddSharpInspectCameraRecovery(_ => fixture.Engine);
        Assert.Throws<ArgumentException>(() => services.AddSharpInspectCameraAcquisition(_ =>
            throw new InvalidOperationException("FactoryMustNotRun")));
        Assert.Throws<ArgumentException>(() => services.AddSharpInspectCameraRecovery(_ => fixture.Engine));
        var request = new CameraRebindRequest(Guid.NewGuid(), new(CommandSource.PhysicalConsole),
            "Primary", 0, null, fixture.Target, "ConflictingSetup");
        var result = await fixture.Runtime.RebindAsync(request);
        Assert.False(result.Succeeded);
        Assert.Equal("CameraRecoveryOwnsDevice", result.ReasonCode);
        Assert.Empty(fixture.Provider.OpenedIdentities);
        var apply = await fixture.Runtime.ApplyDebugConfigurationAsync(new(Guid.NewGuid(),
            new(CommandSource.PhysicalConsole), "Primary", 0, null, Requested, "ConflictingSetup"));
        Assert.False(apply.Succeeded);
        Assert.Equal("CameraRecoveryOwnsDevice", apply.ReasonCode);
        Assert.Equal(0, fixture.Initial.ApplyCount);
    }

    [Fact]
    public async Task V119_I04_UnavailableHealthActivatesSourceAlarmWithoutOpeningDevice()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Until(async () => (await fixture.Runtime.GetSnapshotAsync()).Camera.Connection == HealthState.Healthy);
        fixture.Initial.ThrowHealth = true;
        await Until(async () => (await fixture.Runtime.GetSnapshotAsync()).CameraRecovery is
            { SourceHealthy: false, Health: null });
        var snapshot = await fixture.Runtime.GetSnapshotAsync();
        Assert.Equal(HealthState.Unknown, snapshot.Camera.Connection);
        Assert.Equal(HealthState.Unknown, snapshot.Camera.Configuration);
        Assert.Equal(HealthState.Unknown, snapshot.Camera.Acquisition);
        Assert.False(snapshot.Ready);
        Assert.Empty(fixture.Provider.OpenedIdentities);
        await Until(async () => (await fixture.Runtime.GetSnapshotAsync()).AlarmState!.Instances.Any(
            instance => instance.Code == "CameraDisconnected" && !instance.SourceHealthy &&
                instance.Lifecycle == AlarmLifecycle.Active));
    }

    private static readonly RequestedCameraConfiguration Requested = new(
        ProductionAcquisitionMode.SoftwareTrigger, 10, 0, new(0, 0, 1, 1),
        VisionPixelFormat.Mono8, null, 100, 0, null);

    private static async Task Until(Func<bool> condition) =>
        await Until(() => Task.FromResult(condition()));

    private static async Task Until(Func<Task<bool>> condition)
    {
        var started = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(15), "Camera recovery condition timed out.");
            await Task.Delay(10);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _container;
        private readonly AuditIntegrityPolicy _audit;
        private Fixture(ServiceProvider container, AuditIntegrityPolicy audit, TestClock clock,
            TestProvider provider, TestDevice initial, CameraBindingTarget target, CameraRecoveryService engine)
        {
            _container = container; _audit = audit; Clock = clock; Provider = provider;
            Initial = initial; Target = target; Engine = engine;
            Runtime = (StationRuntime)container.GetRequiredService<IStationRuntime>();
        }
        internal StationRuntime Runtime { get; }
        internal TestClock Clock { get; }
        internal TestProvider Provider { get; }
        internal TestDevice Initial { get; }
        internal CameraBindingTarget Target { get; }
        internal CameraRecoveryService Engine { get; }

        internal static async Task<Fixture> CreateAsync(bool includeRecoveryMapping = true)
        {
            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V119-Station",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var audit = new AuditIntegrityPolicy("V119Station", "1", "SharpInspect.Test.V119." + Guid.NewGuid().ToString("N"))
            { AllowInitialKeyCreation = true, KeyDirectory = Path.Combine(directory, "keys"),
                CheckpointEveryEntries = 2, VerificationInterval = TimeSpan.FromSeconds(1) };
            var prerequisites = AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution |
                AlarmResetPrerequisites.NoPendingDelivery;
            var rules = new List<AlarmPolicyRule> { new("StartupRecoveryRequired", "Runtime.StartupRecovery",
                AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared,
                null, ResetPrerequisites: prerequisites) };
            // Literal expected codes deliberately independent from Runtime mapping helpers.
            foreach (var code in new[] { "CameraEarlyFrame", "CameraExtraFrame", "CameraLateFrame",
                "CameraCorrelationMismatch", "CameraEarlyHardwarePulse", "CameraDuplicateHardwarePulse",
                "CameraTriggerWhileBusy", "CameraInvalidFrame", "CameraObservationGap" })
                rules.Add(new(code, "Runtime.CameraAcquisition", AlarmSeverity.Error,
                    ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: prerequisites));
            if (includeRecoveryMapping)
                foreach (var code in new[] { "CameraDisconnected", "CameraRecoveryFailed" })
                    rules.Add(new(code, "Runtime.CameraRecovery", AlarmSeverity.Error,
                        ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null,
                        ResetPrerequisites: prerequisites));
            var options = new ProductionStoreOptions(Path.Combine(directory, "trace.sqlite"))
            {
                AuditIntegrityPolicy = audit,
                LocalIdentity = new LocalIdentityOptions("V119Station", new LocalPasswordPolicy
                { Blocklist = PasswordBlocklist.Create("V119-blocklist", "1", new[] { "known-compromised-value" }) },
                    new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, AuthorizationPolicy.Development),
                AlarmPolicy = new AlarmPolicy("V119-development", "1", rules, TimeSpan.FromSeconds(10))
            };
            var clock = new TestClock();
            var provider = new TestProvider(clock);
            var initial = new TestDevice(provider.Identity, clock, true);
            var acquisition = new CameraAcquisitionService(initial,
                initial.Capabilities.ValidateConfiguration(Requested).Effective!, clock,
                new CameraAcquisitionOptions(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)));
            var target = new CameraBindingTarget(provider.Identity, "Camera.One");
            var engine = new CameraRecoveryService(provider, target, "Primary", Requested, acquisition, clock,
                new CameraRecoveryOptions(TimeSpan.FromMilliseconds(10), 1, TimeSpan.FromMilliseconds(200),
                    TimeSpan.FromMilliseconds(200)));
            var services = new ServiceCollection();
            services.AddSharpInspectCameraRecovery(_ => engine);
            services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(20));
            var container = services.BuildServiceProvider();
            var fixture = new Fixture(container, audit, clock, provider, initial, target, engine);
            var store = container.GetRequiredService<SqliteCommandStore>();
            var initialization = await store.Initialization;
            Assert.True(initialization.Committed, initialization.ReasonCode);
            await Until(() => store.Integrity?.State == AuditIntegrityState.Verified);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await _container.DisposeAsync();
            var key = WindowsMachineAuditKey.GetKeyPath(_audit);
            if (File.Exists(key)) File.Delete(key);
        }
    }

    private sealed class TestClock : IFrameAcquisitionClock
    {
        private readonly object _sync = new();
        private readonly List<Scheduled> _events = new();
        private long _now;
        public long Frequency => TimeSpan.TicksPerSecond;
        public FrameTimePoint GetTimePoint()
        { lock (_sync) return new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(_now), _now); }
        public IDisposable Schedule(long dueTimestamp, FrameAcquisitionClockPhase phase, Action callback)
        {
            var scheduled = new Scheduled(dueTimestamp, phase, callback);
            lock (_sync) _events.Add(scheduled);
            return scheduled;
        }
        internal void AdvanceTo(long timestamp)
        {
            while (true)
            {
                Scheduled? next;
                lock (_sync)
                {
                    Assert.True(timestamp >= _now);
                    next = _events.Where(item => !item.Cancelled && item.Due <= timestamp)
                        .OrderBy(item => item.Due).ThenBy(item => item.Phase).FirstOrDefault();
                    if (next is null) { _now = timestamp; return; }
                    _events.Remove(next); _now = next.Due;
                }
                next.Callback();
            }
        }
        private sealed class Scheduled : IDisposable
        {
            internal Scheduled(long due, FrameAcquisitionClockPhase phase, Action callback)
            { Due = due; Phase = phase; Callback = callback; }
            internal long Due { get; }
            internal FrameAcquisitionClockPhase Phase { get; }
            internal Action Callback { get; }
            internal bool Cancelled;
            public void Dispose() => Cancelled = true;
        }
    }

    private sealed class TestProvider : ICameraProvider
    {
        private readonly TestClock _clock;
        internal TestProvider(TestClock clock) => _clock = clock;
        internal int OpenFailures;
        internal List<string> OpenedIdentities { get; } = new();
        internal TestDevice? LastOpened;
        public CameraProviderIdentity Identity { get; } = new("Fixture", "1", "Fixture.Camera", "1");
        public ValueTask<CameraDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("RecoveryMustNotDiscoverReplacement");
        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity, CancellationToken cancellationToken = default)
        {
            OpenedIdentities.Add(stableDeviceIdentity);
            if (OpenFailures-- > 0) return ValueTask.FromResult(CameraOpenResult.Failure("FixtureDeviceMissing"));
            LastOpened = new TestDevice(Identity, _clock, false);
            return ValueTask.FromResult(CameraOpenResult.Success(LastOpened));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestDevice : IControlledCameraDevice
    {
        private readonly TestClock _clock;
        private readonly Guid _epoch = Guid.NewGuid();
        private CameraHealthSnapshot _health;
        internal int ApplyCount;
        internal int StartCount;
        internal TestDevice(CameraProviderIdentity provider, TestClock clock, bool armed)
        {
            _clock = clock; Descriptor = new(provider, "Camera.One", "Fixture camera");
            _health = new(CameraProviderAvailability.Available, CameraConnectionState.Open,
                armed ? CameraConfigurationState.Applied : CameraConfigurationState.Unconfigured,
                armed ? CameraAcquisitionState.Armed : CameraAcquisitionState.Stopped, clock.GetTimePoint());
        }
        public CameraDeviceDescriptor Descriptor { get; }
        public CameraCapabilities Capabilities { get; } = new(new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new CameraDoubleCapability(1, 100, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            new CameraRoiCapabilities(1, 1, new(0, 0, 1), new(0, 0, 1), new(1, 1, 1), new(1, 1, 1)));
        internal volatile bool ThrowHealth;
        public CameraHealthSnapshot GetHealthSnapshot() => ThrowHealth
            ? throw new InvalidOperationException("FixtureHealthUnavailable") : Volatile.Read(ref _health);
        internal void Disconnect() => Volatile.Write(ref _health, new(CameraProviderAvailability.Available,
            CameraConnectionState.Disconnected, CameraConfigurationState.Unknown, CameraAcquisitionState.Stopped,
            _clock.GetTimePoint(), new(CameraFaultClassification.ConnectionLost, "FixtureDisconnected")));
        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(RequestedCameraConfiguration requested,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            Volatile.Write(ref _health, new(CameraProviderAvailability.Available, CameraConnectionState.Open,
                CameraConfigurationState.Applied, CameraAcquisitionState.Stopped, _clock.GetTimePoint()));
            return ValueTask.FromResult(Capabilities.ValidateConfiguration(requested));
        }
        public ValueTask<CameraOperationResult> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            Volatile.Write(ref _health, new(CameraProviderAvailability.Available, CameraConnectionState.Open,
                CameraConfigurationState.Applied, CameraAcquisitionState.Armed, _clock.GetTimePoint()));
            return ValueTask.FromResult(new CameraOperationResult(true, "FixtureArmed"));
        }
        public ValueTask<CameraOperationResult> StopAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CameraOperationResult(true, "FixtureStopped"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence, int maximumCount = 64) =>
            new(_epoch, 1, 0, false, Array.Empty<CameraProtocolObservation>());
        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            FrameAcquisitionControl control, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
