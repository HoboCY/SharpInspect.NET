#pragma warning disable CA1416
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ControlledAcquisitionAlarmTests
{
    private const string ExpectedSource = "Runtime.CameraAcquisition";
    private static readonly IReadOnlyDictionary<CameraProtocolViolationKind, string> ExpectedCodes =
        new Dictionary<CameraProtocolViolationKind, string>
        {
            [CameraProtocolViolationKind.EarlyFrame] = "CameraEarlyFrame",
            [CameraProtocolViolationKind.ExtraFrame] = "CameraExtraFrame",
            [CameraProtocolViolationKind.LateFrame] = "CameraLateFrame",
            [CameraProtocolViolationKind.CorrelationMismatch] = "CameraCorrelationMismatch",
            [CameraProtocolViolationKind.EarlyHardwarePulse] = "CameraEarlyHardwarePulse",
            [CameraProtocolViolationKind.DuplicateHardwarePulse] = "CameraDuplicateHardwarePulse",
            [CameraProtocolViolationKind.TriggerWhileBusy] = "CameraTriggerWhileBusy",
            [CameraProtocolViolationKind.InvalidFrame] = "CameraInvalidFrame",
            [CameraProtocolViolationKind.ObservationGap] = "CameraObservationGap"
        };

    [Fact]
    public async Task V118_A01_ProtocolFactsReachSignedHistoryOnceWithoutOpeningProduction()
    {
        await using var fixture = await Fixture.CreateAsync();
        var kinds = Enum.GetValues<CameraProtocolViolationKind>();
        foreach (var kind in kinds) fixture.Device.Add(kind);
        await WaitAsync(async () => (await fixture.Runtime.GetSnapshotAsync()).AlarmState?.Instances.Count(
            item => item.Source == ExpectedSource) == kinds.Length);
        await fixture.WaitVerifiedAsync();
        var state = await fixture.Runtime.GetSnapshotAsync();
        Assert.False(state.Ready);
        Assert.False(state.Busy);
        Assert.Null(state.CurrentExecution);
        Assert.Contains("ProductionCycleUnavailable", state.AdmissionBlockers);
        Assert.Contains("CameraAcquisitionProtocolFault", state.AdmissionBlockers);
        foreach (var kind in kinds)
        {
            var alarm = Assert.Single(state.AlarmState!.Instances,
                item => item.Code == ExpectedCodes[kind]);
            Assert.Equal(ExpectedSource, alarm.Source);
            Assert.True(alarm.IsLatched);
            Assert.Equal(ProductionImpact.BlockNewTriggers, alarm.ProductionImpact);
            Assert.True(alarm.ResetPrerequisites.HasFlag(AlarmResetPrerequisites.RecoveryComplete));
            Assert.True(alarm.ResetPrerequisites.HasFlag(AlarmResetPrerequisites.NoActiveExecution));
            Assert.False(alarm.SourceHealthy);
        }
        var before = await fixture.History.QueryAsync(new AlarmHistoryFilter(pageSize: 100));
        Assert.Equal(kinds.Length, before.Records.Count(record =>
            record.Transition == AlarmTransitionKind.Raised &&
            record.Instance?.Source == ExpectedSource));
        await Task.Delay(200);
        await fixture.WaitVerifiedAsync();
        var after = await fixture.History.QueryAsync(new AlarmHistoryFilter(pageSize: 100));
        Assert.Equal(before.Records.Count, after.Records.Count);
    }

    [Fact]
    public async Task V118_A02_MissingProtocolPolicyMappingFailsClosed()
    {
        await using var fixture = await Fixture.CreateAsync(completeMapping: false);
        var snapshot = await fixture.Runtime.GetSnapshotAsync();
        Assert.False(snapshot.Ready);
        Assert.False(snapshot.AlarmState!.Available);
        Assert.Contains("CameraAcquisitionAlarmMappingUnavailable", snapshot.AdmissionBlockers);
        Assert.Contains("AlarmAuthorityUnavailable", snapshot.AdmissionBlockers);
    }

    [Fact]
    public async Task V118_A03_AdapterCursorGapRaisesAStableAlarmAndDoesNotHideAvailableFacts()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Device.AddBatch(70, CameraProtocolViolationKind.LateFrame);
        await WaitAsync(async () => (await fixture.Runtime.GetSnapshotAsync()).AlarmState?.Instances.Any(
            item => item.Code == "CameraObservationGap") == true);
        await WaitAsync(async () => (await fixture.Runtime.GetSnapshotAsync()).AlarmState?.Instances.Any(
            item => item.Code == "CameraLateFrame") == true);
        Assert.False((await fixture.Runtime.GetSnapshotAsync()).Ready);
    }

    [Fact]
    public async Task V118_A04_RegistrationIsSingleAndAsyncProviderDisposesTheOwnedDevice()
    {
        var device = new ProtocolDevice();
        var services = new ServiceCollection();
        var creates = 0;
        services.AddSharpInspectCameraAcquisition(_ =>
        {
            Interlocked.Increment(ref creates);
            return CreateService(device);
        });
        Assert.Throws<ArgumentException>(() => services.AddSharpInspectCameraAcquisition(_ => CreateService(device)));
        await using (var provider = services.BuildServiceProvider())
        {
            var first = provider.GetRequiredService<CameraAcquisitionService>();
            Assert.Same(first, provider.GetRequiredService<CameraAcquisitionService>());
            Assert.Equal(1, creates);
        }
        Assert.Equal(1, device.DisposeCount);
        Assert.Equal(1, device.StopCount);
    }

    private static CameraAcquisitionService CreateService(ProtocolDevice device) => new(device,
        ProtocolDevice.Effective, new ProtocolClock(),
        new CameraAcquisitionOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("CameraAlarmExpectationNotObserved");
            await Task.Delay(25);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AuditIntegrityPolicy _audit;
        internal ProtocolDevice Device { get; }
        internal IStationRuntime Runtime { get; }
        internal IAlarmHistoryQuery History { get; }
        private SqliteCommandStore Store { get; }
        private Fixture(ServiceProvider provider, AuditIntegrityPolicy audit, ProtocolDevice device)
        {
            _provider = provider; _audit = audit; Device = device;
            Store = provider.GetRequiredService<SqliteCommandStore>();
            Runtime = provider.GetRequiredService<IStationRuntime>();
            History = provider.GetRequiredService<IAlarmHistoryQuery>();
        }
        internal static async Task<Fixture> CreateAsync(bool completeMapping = true)
        {
            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V118-Alarms",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var audit = new AuditIntegrityPolicy("V118AlarmStation", "v1", "SharpInspect.Test.V118." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true, KeyDirectory = Path.Combine(directory, "keys"),
                CheckpointEveryEntries = 2, VerificationInterval = TimeSpan.FromSeconds(1)
            };
            var rules = new List<AlarmPolicyRule> { new("StartupRecoveryRequired", "Runtime.StartupRecovery",
                AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null) };
            if (completeMapping)
                rules.AddRange(Enum.GetValues<CameraProtocolViolationKind>().Select(kind => new AlarmPolicyRule(
                    ExpectedCodes[kind], ExpectedSource,
                    AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared,
                    null, ResetPrerequisites: AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution)));
            var options = new ProductionStoreOptions(Path.Combine(directory, "alarms.sqlite"))
            {
                AuditIntegrityPolicy = audit,
                LocalIdentity = new LocalIdentityOptions("V118AlarmStation", new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("V118-blocklist", "1", new[] { "known-compromised-value" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, AuthorizationPolicy.Development),
                AlarmPolicy = new AlarmPolicy("V118-development", "1", rules, TimeSpan.FromSeconds(10))
            };
            var device = new ProtocolDevice();
            var services = new ServiceCollection();
            services.AddSharpInspectCameraAcquisition(_ => CreateService(device));
            services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(20));
            var fixture = new Fixture(services.BuildServiceProvider(), audit, device);
            var result = await fixture.Store.Initialization;
            Assert.True(result.Committed, result.ReasonCode);
            await fixture.WaitVerifiedAsync();
            await WaitAsync(async () => (await fixture.Runtime.GetSnapshotAsync()).AlarmState is not null);
            return fixture;
        }
        internal Task WaitVerifiedAsync() => WaitAsync(() =>
            Task.FromResult(Store.Integrity?.State == AuditIntegrityState.Verified));
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            var path = Path.Combine(_audit.KeyDirectory, Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(_audit.SigningKeyName))) + ".key");
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class ProtocolClock : IFrameAcquisitionClock
    {
        public long Frequency => TimeSpan.TicksPerSecond;
        public FrameTimePoint GetTimePoint() => new(DateTimeOffset.UtcNow, 0);
        public IDisposable Schedule(long dueTimestamp, FrameAcquisitionClockPhase phase, Action callback) =>
            throw new InvalidOperationException("AlarmFixtureMustNotAcquireFrames");
    }

    private sealed class ProtocolDevice : IControlledCameraDevice
    {
        private readonly object _sync = new();
        private readonly Queue<CameraProtocolObservation> _history = new();
        private readonly Guid _epoch = Guid.NewGuid();
        private long _sequence;
        internal int StopCount;
        internal int DisposeCount;
        internal static EffectiveCameraConfiguration Effective { get; } = new(
            ProductionAcquisitionMode.SoftwareTrigger, 10, 0, new RegionOfInterest(0, 0, 1, 1),
            VisionPixelFormat.Mono8, null, 100, 0, null);
        public CameraDeviceDescriptor Descriptor { get; } = new(new("Fixture", "1", "Fixture.Adapter", "1"),
            "Device.One", "Protocol fixture");
        public CameraCapabilities Capabilities { get; } = new(new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new CameraDoubleCapability(1, 100, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            new CameraRoiCapabilities(1, 1, new(0, 0, 1), new(0, 0, 1), new(1, 1, 1), new(1, 1, 1)));
        public CameraHealthSnapshot GetHealthSnapshot() => new(CameraProviderAvailability.Available,
            CameraConnectionState.Open, CameraConfigurationState.Applied, CameraAcquisitionState.Armed,
            new(DateTimeOffset.UtcNow, 0));
        internal void Add(CameraProtocolViolationKind kind) => AddBatch(1, kind);
        internal void AddBatch(int count, CameraProtocolViolationKind kind)
        {
            lock (_sync)
                for (var i = 0; i < count; i++)
                {
                    _history.Enqueue(new(++_sequence, kind, "FixtureProtocolViolation", new(DateTimeOffset.UtcNow, _sequence)));
                    while (_history.Count > 64) _history.Dequeue();
                }
        }
        public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence, int maximumCount = 64)
        {
            lock (_sync)
            {
                var first = _history.Count == 0 ? _sequence + 1 : _history.Peek().Sequence;
                return new(_epoch, first, _sequence, afterSequence < first - 1,
                    _history.Where(item => item.Sequence > afterSequence).Take(maximumCount));
            }
        }
        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(RequestedCameraConfiguration requested,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<CameraOperationResult> StartAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            FrameAcquisitionControl control, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<CameraOperationResult> StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref StopCount);
            return ValueTask.FromResult(new CameraOperationResult(true, "FixtureStopped"));
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
    }
}
