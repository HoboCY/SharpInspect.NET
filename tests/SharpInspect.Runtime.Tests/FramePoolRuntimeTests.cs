#pragma warning disable CA1416

using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Focused runtime acceptance for the bounded frame pool boundary.  The pool itself is
/// deliberately exercised as an acquisition adapter would exercise it; the host has no
/// API which can report a healthy source or clear the production latch.
/// </summary>
public sealed class FramePoolRuntimeTests
{
    [Fact]
    public async Task V111_R01_ProductionExhaustionFaultsSnapshotAndRemainsLatchedAfterLeaseReturn()
    {
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 1024, TimeSpan.FromSeconds(1)));
        await using var runtime = new StationRuntime(null, TimeSpan.FromMilliseconds(20), null, null, pool);
        var input = CreateInput(ExecutionKind.Production);
        var source = new byte[(int)input.Metadata.RequiredBufferLength];

        var first = pool.TryCopyFrame(input.Metadata, input.Provenance, source);
        Assert.True(first.Succeeded, first.ReasonCode);
        Assert.NotNull(first.Lease);

        var exhausted = pool.TryCopyFrame(input.Metadata, input.Provenance, source);
        Assert.False(exhausted.Succeeded);
        Assert.Equal("FrameBufferExhausted", exhausted.ReasonCode);
        Assert.True(pool.ProductionFaultLatched);

        var faulted = await runtime.GetSnapshotAsync();
        Assert.Equal(HealthState.Faulted, faulted.Camera.Buffers);
        Assert.False(faulted.Ready);
        Assert.Contains("FrameBufferExhausted", faulted.AdmissionBlockers);
        Assert.Equal(ProductionArmState.Disarmed, faulted.ArmState);

        first.Lease!.Dispose();
        var afterReturn = pool.TryCopyFrame(input.Metadata, input.Provenance, source);
        Assert.False(afterReturn.Succeeded);
        Assert.Equal("FrameBufferExhausted", afterReturn.ReasonCode);
        Assert.True(pool.GetSnapshot().ProductionFaultLatched);

        var stillFaulted = await runtime.GetSnapshotAsync();
        Assert.Equal(HealthState.Faulted, stillFaulted.Camera.Buffers);
        Assert.Contains("FrameBufferExhausted", stillFaulted.AdmissionBlockers);
        Assert.False(stillFaulted.Ready);
    }

    [Fact]
    public async Task V111_R02_ExplicitPoolRegistrationWiresTheSameBoundedPoolIntoRuntime()
    {
        var services = new ServiceCollection();
        var options = new FrameBufferPoolOptions(1, 1024, TimeSpan.FromSeconds(1));
        services.AddSharpInspectFrameBufferPool(options);
        var duplicate = Assert.Throws<ArgumentException>(() => services.AddSharpInspectFrameBufferPool(
            new FrameBufferPoolOptions(2, 1024, TimeSpan.FromSeconds(1))));
        Assert.Contains("FrameBufferPoolAlreadyRegistered", duplicate.Message);
        services.AddSharpInspectRuntime(TimeSpan.FromMilliseconds(20));
        await using var provider = services.BuildServiceProvider();

        var pool = provider.GetRequiredService<FrameBufferPool>();
        var runtime = provider.GetRequiredService<IStationRuntime>();
        var concrete = Assert.IsType<StationRuntime>(runtime);
        var input = CreateInput(ExecutionKind.Production);
        using var held = pool.TryCopyFrame(input.Metadata, input.Provenance,
            new byte[(int)input.Metadata.RequiredBufferLength]).Lease!;
        var exhausted = pool.TryCopyFrame(input.Metadata, input.Provenance,
            new byte[(int)input.Metadata.RequiredBufferLength]);

        Assert.False(exhausted.Succeeded);
        Assert.Equal("FrameBufferExhausted", exhausted.ReasonCode);
        var snapshot = await WaitForSnapshotAsync(runtime, state =>
            state.Camera.Buffers == HealthState.Faulted && state.AdmissionBlockers.Contains("FrameBufferExhausted"));
        Assert.Equal(HealthState.Faulted, snapshot.Camera.Buffers);
        Assert.NotNull(concrete.GetFrameBufferPoolSnapshot());
        Assert.True(concrete.GetFrameBufferPoolSnapshot()!.ProductionFaultLatched);
    }

    [Fact]
    public async Task V111_R03_SignedAlarmIsRaisedByTrustedFrameSourceAndFreeingLeaseDoesNotClearIt()
    {
        await using var fixture = await FrameRuntimeFixture.CreateAsync("correct");
        var input = CreateInput(ExecutionKind.Production);
        var source = new byte[(int)input.Metadata.RequiredBufferLength];
        var first = fixture.Pool.TryCopyFrame(input.Metadata, input.Provenance, source);
        Assert.True(first.Succeeded, first.ReasonCode);
        Assert.NotNull(first.Lease);

        var exhausted = fixture.Pool.TryCopyFrame(input.Metadata, input.Provenance, source);
        Assert.False(exhausted.Succeeded);
        Assert.Equal("FrameBufferExhausted", exhausted.ReasonCode);

        var faulted = await WaitForSnapshotAsync(fixture.Runtime, state =>
            state.Camera.Buffers == HealthState.Faulted &&
            state.AlarmState?.Instances.Any(instance => instance.Code == "FrameBufferExhausted" &&
                instance.Lifecycle == AlarmLifecycle.Active) == true);
        Assert.False(faulted.Ready);
        Assert.Contains("FrameBufferExhausted", faulted.AdmissionBlockers);
        Assert.Equal("Runtime.FrameBufferPool", Assert.Single(faulted.AlarmState!.Instances,
            instance => instance.Code == "FrameBufferExhausted").Source);

        await fixture.WaitVerifiedAsync();
        var history = await fixture.AlarmHistory.QueryAsync(new AlarmHistoryFilter(
            code: "FrameBufferExhausted", pageSize: 100));
        Assert.True(history.Available, history.ReasonCode);
        Assert.Contains(history.Records, record => record.Code == "FrameBufferExhausted" &&
            record.Transition == AlarmTransitionKind.Raised && record.Source == "Runtime.FrameBufferPool");

        first.Lease!.Dispose();
        var afterReturn = fixture.Pool.TryCopyFrame(input.Metadata, input.Provenance, source);
        Assert.False(afterReturn.Succeeded);
        Assert.Equal("FrameBufferExhausted", afterReturn.ReasonCode);
        var stillLatched = await fixture.Runtime.GetSnapshotAsync();
        Assert.Equal(HealthState.Faulted, stillLatched.Camera.Buffers);
        Assert.Contains(stillLatched.AlarmState!.Instances, instance =>
            instance.Code == "FrameBufferExhausted" && instance.IsLatched &&
            instance.Lifecycle == AlarmLifecycle.Active);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-source")]
    public async Task V111_R04_MissingOrWrongFrameAlarmMappingIsUnavailableAndBlocksAdmission(string mapping)
    {
        await using var fixture = await FrameRuntimeFixture.CreateAsync(mapping);

        var unavailable = await WaitForSnapshotAsync(fixture.Runtime, state =>
            state.AlarmState is { Available: false, ReasonCode: "FrameBufferAlarmMappingUnavailable" });
        Assert.False(unavailable.Ready);
        Assert.Contains("AlarmAuthorityUnavailable", unavailable.AdmissionBlockers);
        Assert.False(fixture.Pool.ProductionFaultLatched);
        Assert.DoesNotContain(unavailable.AlarmState!.Instances,
            instance => instance.Code == "FrameBufferExhausted");
    }

    [Fact]
    public async Task V111_R05_HealthyPoolIsRenewedAcrossMaintenanceCyclesWithoutRaisingAlarm()
    {
        await using var fixture = await FrameRuntimeFixture.CreateAsync("correct",
            TimeSpan.FromMilliseconds(500));

        await WaitForSnapshotAsync(fixture.Runtime, state =>
            state.AlarmState is { Available: true } &&
            !state.AlarmState.Instances.Any(instance => instance.Code == "FrameBufferExhausted"));
        var concrete = Assert.IsType<StationRuntime>(fixture.Runtime);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        long nextSequence;
        do
        {
            nextSequence = concrete.NextAlarmObservationSequence("FrameBufferExhausted");
            if (nextSequence >= 3) break;
            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);

        Assert.True(nextSequence >= 3,
            "The trusted healthy pool source was not renewed across two maintenance cycles.");
        var snapshot = await fixture.Runtime.GetSnapshotAsync();
        Assert.True(snapshot.AlarmState!.Available, snapshot.AlarmState.ReasonCode);
        Assert.DoesNotContain(snapshot.AlarmState.Instances,
            instance => instance.Code == "FrameBufferExhausted");
        Assert.False(fixture.Pool.ProductionFaultLatched);
    }

    [Fact]
    public async Task V111_R06_ProductionLayoutOverflowLatchesFaultBeforeClaimingFreePoolSlot()
    {
        await using var fixture = await FrameRuntimeFixture.CreateAsync("correct");
        var oversized = CreateInput(ExecutionKind.Production, width: 33, height: 33, stride: 33);
        var failed = fixture.Pool.TryCopyFrame(oversized.Metadata, oversized.Provenance,
            new byte[(int)oversized.Metadata.RequiredBufferLength]);

        Assert.False(failed.Succeeded);
        Assert.Equal("FrameBufferExhausted", failed.ReasonCode);
        Assert.Equal(ExecutionStatus.Error, failed.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, failed.Decision);
        Assert.Null(failed.Lease);
        Assert.True(fixture.Pool.ProductionFaultLatched);

        var immediate = await fixture.Runtime.GetSnapshotAsync();
        Assert.Equal(HealthState.Faulted, immediate.Camera.Buffers);
        Assert.False(immediate.Ready);
        Assert.Contains("FrameBufferExhausted", immediate.AdmissionBlockers);

        var alarmed = await WaitForSnapshotAsync(fixture.Runtime, state =>
            state.AlarmState?.Instances.Any(instance => instance.Code == "FrameBufferExhausted" &&
                instance.Lifecycle == AlarmLifecycle.Active) == true);
        Assert.False(alarmed.Ready);
        await fixture.WaitVerifiedAsync();
        var history = await fixture.AlarmHistory.QueryAsync(new AlarmHistoryFilter(
            code: "FrameBufferExhausted", pageSize: 100));
        Assert.True(history.Available, history.ReasonCode);
        Assert.Contains(history.Records, record => record.Code == "FrameBufferExhausted" &&
            record.Transition == AlarmTransitionKind.Raised &&
            record.Source == "Runtime.FrameBufferPool");

        var valid = CreateInput(ExecutionKind.Production);
        var stillRejected = fixture.Pool.TryCopyFrame(valid.Metadata, valid.Provenance,
            new byte[(int)valid.Metadata.RequiredBufferLength]);
        Assert.False(stillRejected.Succeeded);
        Assert.Equal("FrameBufferExhausted", stillRejected.ReasonCode);
        Assert.True(fixture.Pool.ProductionFaultLatched);
    }

    [Fact]
    public async Task V111_R07_ExternalFrameBufferCodeStillExpiresThroughGenericTrustedSource()
    {
        await using var fixture = await FrameRuntimeFixture.CreateAsync("external-source",
            TimeSpan.FromMilliseconds(200), registerPool: false);
        var runtime = Assert.IsType<StationRuntime>(fixture.Runtime);
        runtime.RegisterAlarmSource("Camera.Primary");
        var epoch = (await fixture.Runtime.GetSnapshotAsync()).RuntimeEpoch;

        var unhealthySequence = runtime.NextAlarmObservationSequence("FrameBufferExhausted");
        var raised = await runtime.ObserveAlarmAsync(new AlarmObservation(epoch, unhealthySequence,
            "FrameBufferExhausted", "Camera.Primary", false, DateTimeOffset.UtcNow));
        Assert.True(raised.Accepted, raised.ReasonCode);
        var raisedInstance = await WaitForSnapshotAsync(fixture.Runtime, state =>
            state.AlarmState?.Instances.Any(instance => instance.Code == "FrameBufferExhausted" &&
                instance.Source == "Camera.Primary" && instance.Lifecycle == AlarmLifecycle.Active) == true);
        var instanceId = Assert.Single(raisedInstance.AlarmState!.Instances,
            instance => instance.Code == "FrameBufferExhausted").InstanceId;

        var healthySequence = runtime.NextAlarmObservationSequence("FrameBufferExhausted");
        var healthy = await runtime.ObserveAlarmAsync(new AlarmObservation(epoch, healthySequence,
            "FrameBufferExhausted", "Camera.Primary", true, DateTimeOffset.UtcNow));
        Assert.True(healthy.Accepted, healthy.ReasonCode);
        await WaitForSnapshotAsync(fixture.Runtime, state => state.AlarmState?.Instances.Any(instance =>
            instance.InstanceId == instanceId && instance.Lifecycle == AlarmLifecycle.RecoveredLatched &&
            instance.SourceHealthy) == true);

        var expired = await WaitForSnapshotAsync(fixture.Runtime, state => state.AlarmState?.Instances.Any(
            instance => instance.InstanceId == instanceId && instance.Lifecycle == AlarmLifecycle.Active &&
                !instance.SourceHealthy && instance.Source == "Camera.Primary" &&
                instance.SourceObservationSequence > healthySequence) == true);
        Assert.False(expired.Ready);
        Assert.Contains("AlarmProductionBlocked", expired.AdmissionBlockers);

        await fixture.WaitVerifiedAsync();
        var history = await fixture.AlarmHistory.QueryAsync(new AlarmHistoryFilter(
            code: "FrameBufferExhausted", pageSize: 100));
        Assert.True(history.Available, history.ReasonCode);
        Assert.Contains(history.Records, record => record.Code == "FrameBufferExhausted" &&
            record.Source == "Camera.Primary" && record.Transition == AlarmTransitionKind.Observed);
    }

    private static FrameInput CreateInput(ExecutionKind kind, int width = 2, int height = 2,
        int stride = 2)
    {
        var correlation = new ExecutionCorrelationId(kind, Guid.NewGuid());
        var configuration = new EffectiveCameraConfiguration(
            ProductionAcquisitionMode.HardwareTrigger, 500, 1.5,
            new RegionOfInterest(0, 0, width, height), VisionPixelFormat.Mono8, null,
            500, 0, null);
        var metadata = new FrameMetadata(correlation, "TopCamera", width, height, stride,
            VisionPixelFormat.Mono8, null, Utc(10), configuration);
        var provenance = new FrameProvenance(correlation, "vendor-a", "1", "adapter-a", "1",
            "sdk-a", "1", null, "device-1", null, null, "Mono8", "normalized-v1",
            false, false, null, null, Milestones());
        return new FrameInput(metadata, provenance);
    }

    private static async Task<StationStateSnapshot> WaitForSnapshotAsync(IStationRuntime runtime,
        Func<StationStateSnapshot, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        StationStateSnapshot last = await runtime.GetSnapshotAsync();
        while (DateTime.UtcNow < deadline)
        {
            last = await runtime.GetSnapshotAsync();
            if (predicate(last)) return last;
            await Task.Delay(25);
        }

        throw new XunitException("Snapshot predicate was not met. Last revision=" + last.Revision +
            ", ready=" + last.Ready + ", cameraBuffers=" + last.Camera.Buffers +
            ", alarm=" + last.AlarmState?.ReasonCode);
    }

    private static FrameAcquisitionMilestones Milestones() =>
        new(1_000_000, new FrameTimePoint(Utc(1), 10),
            new FrameTimePoint(Utc(2), 12), new FrameTimePoint(Utc(3), 14),
            new FrameTimePoint(Utc(4), 16));

    private static DateTimeOffset Utc(int second) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(second);

    private sealed record FrameInput(FrameMetadata Metadata, FrameProvenance Provenance);

    private sealed class FrameRuntimeFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private FrameRuntimeFixture(ServiceProvider provider, string directory, AuditIntegrityPolicy auditPolicy,
            ProductionStoreOptions options)
        {
            _provider = provider;
            Directory = directory;
            AuditPolicy = auditPolicy;
            Options = options;
            Store = provider.GetRequiredService<SqliteCommandStore>();
            AlarmHistory = provider.GetRequiredService<IAlarmHistoryQuery>();
            Pool = provider.GetService<FrameBufferPool>()!;
        }

        public string Directory { get; }
        public AuditIntegrityPolicy AuditPolicy { get; }
        public ProductionStoreOptions Options { get; }
        public SqliteCommandStore Store { get; }
        public IStationRuntime Runtime { get; private set; } = null!;
        public IAlarmHistoryQuery AlarmHistory { get; }
        public FrameBufferPool Pool { get; }

        public static async Task<FrameRuntimeFixture> CreateAsync(string frameAlarmMapping,
            TimeSpan? sourceFreshness = null, bool registerPool = true)
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Frame runtime acceptance requires Windows DPAPI machine protection.");

            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V111-FramePool",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var stationId = "V111FrameStation";
            var auditPolicy = new AuditIntegrityPolicy(stationId, "v1",
                "SharpInspect.Test.V111.Frame." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "audit-keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            var alarmRules = new List<AlarmPolicyRule>
            {
                new("StartupRecoveryRequired", "Runtime.StartupRecovery", AlarmSeverity.Warning,
                    ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null, 100,
                    AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoPendingDelivery)
            };
            if (frameAlarmMapping == "correct")
            {
                alarmRules.Add(new AlarmPolicyRule("FrameBufferExhausted", "Runtime.FrameBufferPool",
                    AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true,
                    AlarmNotification.UntilCleared, null, 200,
                    AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution));
            }
            else if (frameAlarmMapping == "wrong-source")
            {
                alarmRules.Add(new AlarmPolicyRule("FrameBufferExhausted", "Runtime.OtherSource",
                    AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true,
                    AlarmNotification.UntilCleared, null, 200,
                    AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution));
            }
            else if (frameAlarmMapping == "external-source")
            {
                alarmRules.Add(new AlarmPolicyRule("FrameBufferExhausted", "Camera.Primary",
                    AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true,
                    AlarmNotification.UntilCleared, null, 200,
                    AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution));
            }
            else if (frameAlarmMapping != "missing")
            {
                throw new ArgumentOutOfRangeException(nameof(frameAlarmMapping));
            }

            var alarmPolicy = new AlarmPolicy("v111-frame-policy", "development-v1", alarmRules,
                sourceFreshness ?? TimeSpan.FromSeconds(5), maximumActiveInstances: 16, maximumPlcEntries: 1);
            var identityOptions = new LocalIdentityOptions(stationId,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("v111-frame-blocklist", "v1",
                        new[] { "known-compromised-value" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                AuthorizationPolicy.Development);
            var options = new ProductionStoreOptions(Path.Combine(directory, "frame.sqlite"))
            {
                AuditIntegrityPolicy = auditPolicy,
                LocalIdentity = identityOptions,
                AlarmPolicy = alarmPolicy,
                CommitTimeout = TimeSpan.FromSeconds(2),
                QueryTimeout = TimeSpan.FromSeconds(2),
                QueueCapacity = 16
            };

            var services = new ServiceCollection();
            if (registerPool)
                services.AddSharpInspectFrameBufferPool(new FrameBufferPoolOptions(1, 1024, TimeSpan.FromSeconds(1)));
            services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(50));
            var provider = services.BuildServiceProvider();
            try
            {
                var fixture = new FrameRuntimeFixture(provider, directory, auditPolicy, options);
                var initialized = await fixture.Store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await fixture.BootstrapAsync(identityOptions);
                fixture.ResolveAfterBootstrap();
                await fixture.WaitVerifiedAsync();
                await WaitForSnapshotAsync(fixture.Runtime, snapshot => snapshot.AlarmState is not null);
                return fixture;
            }
            catch
            {
                await provider.DisposeAsync();
                throw;
            }
        }

        private async Task BootstrapAsync(LocalIdentityOptions identityOptions)
        {
            var bootstrap = new LocalIdentityService(Store, identityOptions, new TestConsoleAuthority());
            var tokenResult = await bootstrap.ProvisionBootstrapTokenAsync();
            Assert.True(tokenResult.Succeeded, tokenResult.ReasonCode);
            var token = tokenResult.Token!.TakeForDisplay();
            await WaitVerifiedAsync();
            var created = await bootstrap.CreateFirstAdministratorAsync(new BootstrapAdministratorRequest(
                identityOptions.StationId, token, "frame-bootstrap", "V111 Frame Administrator",
                "V111 frame administrator secret 2026!"));
            Assert.True(created.Succeeded, created.ReasonCode);
            created.RecoveryKit?.Dispose();
            await WaitVerifiedAsync();
        }

        private void ResolveAfterBootstrap()
        {
            // Start the Runtime only after the store and identity bootstrap writes have
            // settled. This keeps alarm initialization from racing the fixture setup.
            Runtime = _provider.GetRequiredService<IStationRuntime>();
        }

        public async Task<AuditIntegrityReport> WaitVerifiedAsync(TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
            while (DateTime.UtcNow < deadline)
            {
                var report = Store.Integrity;
                if (report is { State: AuditIntegrityState.Verified }) return report;
                if (report?.State == AuditIntegrityState.Faulted)
                    throw new XunitException("Audit integrity faulted: " + report.ReasonCode);
                await Task.Delay(25);
            }

            throw new XunitException("Audit integrity did not become Verified. Last state: " +
                Store.Integrity?.State + ", reason: " + Store.Integrity?.ReasonCode);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            try
            {
                var keyPath = WindowsMachineAuditKey.GetKeyPath(AuditPolicy);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        private sealed class TestConsoleAuthority : IPhysicalConsoleAuthority
        {
            public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-V111-FRAME-TEST");
        }
    }
}

#pragma warning restore CA1416
