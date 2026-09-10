using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Runtime boundary tests for the admission projection.  The source below is a
/// test-only facts source; it cannot replace the production SQLite writer or make
/// an unconfigured Runtime arm.
/// </summary>
public sealed class ProductionAdmissionRuntimeTests
{
    [Fact]
    public async Task V136_R01_AdmissionReportIsBoundToTheCurrentRuntimeSnapshot()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        await using var runtime = CreateRuntime(new FixtureFactsSource(fixture));

        var report = await runtime.RefreshProductionAdmissionAsync();
        Assert.NotNull(report);
        var snapshot = await runtime.GetSnapshotAsync();
        Assert.Same(report, snapshot.ProductionAdmission);
        Assert.Equal(snapshot.RuntimeEpoch, report!.RuntimeEpoch);
        Assert.Equal(snapshot.Revision, report.SnapshotRevision);
        Assert.True(report.CanArm);
        Assert.False(snapshot.Ready);
        Assert.Equal(ProductionArmState.Disarmed, snapshot.ArmState);
    }

    [Fact]
    public async Task V136_R02_HeartbeatRebindsReportRevisionWithoutChangingEvidence()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        await using var runtime = CreateRuntime(new FixtureFactsSource(fixture),
            TimeSpan.FromMilliseconds(20));

        var first = await runtime.RefreshProductionAdmissionAsync();
        Assert.NotNull(first);
        var firstEvidence = ProductionAdmissionEngine.EvidenceHash(first!);

        await Task.Delay(80);
        var current = await runtime.GetSnapshotAsync();
        var rebound = current.ProductionAdmission;
        Assert.NotNull(rebound);
        Assert.Equal(current.Revision, rebound!.SnapshotRevision);
        Assert.Equal(current.RuntimeEpoch, rebound.RuntimeEpoch);
        Assert.Equal(firstEvidence, ProductionAdmissionEngine.EvidenceHash(rebound));
        Assert.Equal(first!.AdmissionGeneration, rebound.AdmissionGeneration);
    }

    [Fact]
    public async Task V136_R03_AdmissionArmFailsClosedWithoutTheProductionTransactionWriter()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        await using var runtime = CreateRuntime(new FixtureFactsSource(fixture));
        var refresh = await runtime.RefreshProductionAdmissionAsync();
        Assert.NotNull(refresh);

        var outcome = await runtime.SubmitAsync(new ArmProductionCommand(Guid.NewGuid(),
            new CommandInvocation(CommandSource.PhysicalConsole)));

        Assert.Equal(CommandDisposition.Rejected, outcome.Disposition);
        Assert.Equal("ProductionAdmissionConfigurationRequired", outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Unavailable, outcome.Audit);
        var snapshot = await runtime.GetSnapshotAsync();
        Assert.False(snapshot.Ready);
        Assert.Equal(ProductionArmState.Disarmed, snapshot.ArmState);
    }

    [Fact]
    public async Task V136_R04_HeartbeatReevaluatesExpiryAndClosesAdmission()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var expiringStationAcceptance = fixture.Issue(ProductionQualificationLayer.StationAcceptance,
            references: fixture.Records.Where(record => record.Layer != ProductionQualificationLayer.StationAcceptance)
                .ToDictionary(record => record.Layer, record => record.ContentHash),
            expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(500));
        var source = new FixtureFactsSource(fixture,
            () => fixture.Facts(inputs: fixture.Inputs(expiringStationAcceptance)));
        await using var runtime = CreateRuntime(source, TimeSpan.FromMilliseconds(20));

        var initial = await runtime.RefreshProductionAdmissionAsync();
        Assert.NotNull(initial);
        Assert.True(initial!.CanArm);
        var initialGeneration = initial.AdmissionGeneration;
        var initialEvidence = ProductionAdmissionEngine.EvidenceHash(initial);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        ProductionAdmissionReport? expired = null;
        StationStateSnapshot? expiredSnapshot = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
            expiredSnapshot = await runtime.GetSnapshotAsync();
            expired = expiredSnapshot.ProductionAdmission;
            if (expired is not null && expired.Gates.Any(gate =>
                    gate.Gate == ProductionAdmissionGate.StationAcceptance &&
                    gate.Status == ProductionAdmissionGateStatus.Expired))
                break;
        }

        Assert.NotNull(expired);
        var expiredGate = Assert.Single(expired!.Gates, gate =>
            gate.Gate == ProductionAdmissionGate.StationAcceptance);
        Assert.Equal(ProductionAdmissionGateStatus.Expired, expiredGate.Status);
        Assert.NotEqual(initialEvidence, ProductionAdmissionEngine.EvidenceHash(expired));
        Assert.True(expired.AdmissionGeneration > initialGeneration);
        Assert.False(expired.CanArm);
        var snapshot = expiredSnapshot!;
        Assert.Equal(snapshot.Revision, expired.SnapshotRevision);
        Assert.False(snapshot.Ready);
        Assert.Equal(ProductionArmState.Disarmed, snapshot.ArmState);
    }

    [Fact]
    public async Task V136_R05_RefreshReevaluatesChangedFactsAndAdvancesGeneration()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var changed = false;
        var source = new FixtureFactsSource(fixture,
            () => changed
                ? fixture.Facts(missingGate: ProductionAdmissionGate.CameraHealth)
                : fixture.Facts());
        await using var runtime = CreateRuntime(source, TimeSpan.FromMilliseconds(20));

        var initial = await runtime.RefreshProductionAdmissionAsync();
        Assert.NotNull(initial);
        var initialGeneration = initial!.AdmissionGeneration;
        var initialEvidence = ProductionAdmissionEngine.EvidenceHash(initial);
        changed = true;

        var current = await runtime.RefreshProductionAdmissionAsync();
        Assert.NotNull(current);
        var changedGate = Assert.Single(current!.Gates, gate => gate.Gate == ProductionAdmissionGate.CameraHealth);
        Assert.Equal(ProductionAdmissionGateStatus.NotConfigured, changedGate.Status);
        Assert.NotEqual(initialEvidence, ProductionAdmissionEngine.EvidenceHash(current));
        Assert.True(current.AdmissionGeneration > initialGeneration);
        var snapshot = await runtime.GetSnapshotAsync();
        Assert.Equal(snapshot.Revision, current.SnapshotRevision);
        Assert.False(snapshot.Ready);
        Assert.Equal(ProductionArmState.Disarmed, snapshot.ArmState);
    }

    [Fact]
    public async Task V136_R06_FactsCaptureFailureInvalidatesThePreviouslyPassedReport()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var fail = false;
        var source = new FixtureFactsSource(fixture, () => fail
            ? throw new IOException("Virtual facts capture unavailable") : fixture.Facts());
        await using var runtime = CreateRuntime(source, TimeSpan.FromMilliseconds(20));
        var previous = await runtime.RefreshProductionAdmissionAsync();
        Assert.True(previous!.CanArm);
        fail = true;

        var unavailable = await runtime.RefreshProductionAdmissionAsync();

        Assert.NotNull(unavailable);
        Assert.False(unavailable!.CanArm);
        Assert.True(unavailable.AdmissionGeneration > previous.AdmissionGeneration);
        Assert.All(unavailable.Gates.Where(gate => !ProductionAdmissionEngine.IsQualificationGate(gate.Gate)),
            gate =>
            {
                Assert.Equal(ProductionAdmissionGateStatus.Blocked, gate.Status);
                Assert.Equal("ProductionAdmissionFactsUnavailable", gate.ReasonCode);
            });
        var snapshot = await runtime.GetSnapshotAsync();
        Assert.False(snapshot.Ready);
        Assert.Equal(ProductionArmState.Disarmed, snapshot.ArmState);
        Assert.Equal(snapshot.Revision, snapshot.ProductionAdmission!.SnapshotRevision);
        await Task.Delay(70);
        var afterHeartbeat = await runtime.GetSnapshotAsync();
        Assert.True(afterHeartbeat.Revision > snapshot.Revision);
        Assert.All(afterHeartbeat.ProductionAdmission!.Gates.Where(gate =>
            !ProductionAdmissionEngine.IsQualificationGate(gate.Gate)), gate =>
        {
            Assert.Equal(ProductionAdmissionGateStatus.Blocked, gate.Status);
            Assert.Equal("ProductionAdmissionFactsUnavailable", gate.ReasonCode);
        });
        fail = false;
        var refreshed = await runtime.RefreshProductionAdmissionAsync();
        Assert.True(refreshed!.CanArm);
        Assert.DoesNotContain(refreshed.Gates, gate => gate.ReasonCode == "ProductionAdmissionFactsUnavailable");
    }

    [Fact]
    public async Task V136_R07_HeartbeatDuringFactsCaptureDoesNotDiscardTheCompletedObservation()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var source = new DeferredFactsSource(fixture.Facts());
        await using var runtime = CreateRuntime(source, TimeSpan.FromMilliseconds(20));
        var pending = runtime.RefreshProductionAdmissionAsync().AsTask();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var before = await runtime.GetSnapshotAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        StationStateSnapshot current;
        do
        {
            await Task.Delay(10);
            current = await runtime.GetSnapshotAsync();
        } while (current.Revision == before.Revision && DateTime.UtcNow < deadline);
        Assert.True(current.Revision > before.Revision);
        source.Release.TrySetResult(true);

        var report = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(report!.CanArm);
        Assert.True(report.SnapshotRevision > before.Revision);
        Assert.False((await runtime.GetSnapshotAsync()).Ready);
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("faulted")]
    [InlineData("camera")]
    public async Task V136_R08_AuditRecheckBlocksTheReportWithoutPretendingMaterialConfigurationChanged(string next)
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var writer = new AdmissionAuditProbe();
        var cameraChanged = false;
        var source = new FixtureFactsSource(fixture, () => fixture.Facts(
            missingGate: cameraChanged ? ProductionAdmissionGate.CameraHealth : null,
            replacementGate: writer.State switch
            {
                AuditIntegrityState.Verified => new(ProductionAdmissionGate.StoreIntegrity,
                    ProductionAdmissionGateStatus.Passed, "TraceAuditVerified"),
                AuditIntegrityState.Verifying => new(ProductionAdmissionGate.StoreIntegrity,
                    ProductionAdmissionGateStatus.Blocked, "TraceAuditVerificationPending"),
                _ => new(ProductionAdmissionGate.StoreIntegrity,
                    ProductionAdmissionGateStatus.Failed, "TraceAuditUnavailable")
            }));
        await using var runtime = CreateRuntime(source, TimeSpan.FromMilliseconds(20), writer);
        var verified = await runtime.RefreshProductionAdmissionAsync();
        Assert.True(verified!.CanArm);
        writer.State = AuditIntegrityState.Verifying;

        var pending = await runtime.RefreshProductionAdmissionAsync();

        Assert.False(pending!.CanArm);
        Assert.Equal(verified.AdmissionGeneration, pending.AdmissionGeneration);
        Assert.NotEqual(ProductionAdmissionEngine.EvidenceHash(verified), ProductionAdmissionEngine.EvidenceHash(pending));
        Assert.Equal(ProductionAdmissionGateStatus.Blocked,
            pending.Gates.Single(gate => gate.Gate == ProductionAdmissionGate.StoreIntegrity).Status);
        Assert.False((await runtime.GetSnapshotAsync()).Ready);
        await Task.Delay(60);
        Assert.Equal(verified.AdmissionGeneration, (await runtime.GetSnapshotAsync()).ProductionAdmission!.AdmissionGeneration);
        if (next == "camera") cameraChanged = true;
        else writer.State = next == "verified" ? AuditIntegrityState.Verified : AuditIntegrityState.Faulted;

        var result = await runtime.RefreshProductionAdmissionAsync();

        if (next == "verified")
        {
            Assert.True(result!.CanArm);
            Assert.Equal(verified.AdmissionGeneration, result.AdmissionGeneration);
        }
        else
        {
            Assert.False(result!.CanArm);
            Assert.True(result.AdmissionGeneration > verified.AdmissionGeneration);
        }
        Assert.False((await runtime.GetSnapshotAsync()).Ready);
    }

    private sealed class AdmissionAuditProbe : ICommandAuditWriter
    {
        internal AuditIntegrityState State { get; set; } = AuditIntegrityState.Verified;
        public AuditIntegrityReport? Integrity => new(State, "ControlledAuditObservation", null, null,
            0, 0, 0, null, null, DateTimeOffset.UtcNow);
        public Task<StoreWriteResult> Initialization { get; } = Task.FromResult(new StoreWriteResult(true, "Initialized"));
        public TimeSpan CommitTimeout => TimeSpan.FromSeconds(2);
        public ValueTask<StoreWriteResult> AppendAsync(CommandAuditFact fact, StoreDeadline deadline,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("NotATransactionWriter");
    }

    private sealed class DeferredFactsSource : IProductionAdmissionFactsSource
    {
        private readonly ProductionAdmissionFacts _facts;
        internal readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DeferredFactsSource(ProductionAdmissionFacts facts) => _facts = facts;
        public async ValueTask<ProductionAdmissionFacts> CaptureAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return _facts;
        }
    }

    private static StationRuntime CreateRuntime(IProductionAdmissionFactsSource source,
        TimeSpan? heartbeat = null, ICommandAuditWriter? audit = null) => new(audit ?? new ProbeAuditWriter(), heartbeat,
        productionStoreOptions: new ProductionStoreOptions
        {
            ProductionAdmission = new ProductionAdmissionStoreOptions()
        }, productionAdmissionFactsSource: source);

    private sealed class FixtureFactsSource : IProductionAdmissionFactsSource
    {
        private readonly ProductionAdmissionTestFixture _fixture;
        private readonly Func<ProductionAdmissionFacts>? _factory;
        internal FixtureFactsSource(ProductionAdmissionTestFixture fixture,
            Func<ProductionAdmissionFacts>? factory = null)
        {
            _fixture = fixture;
            _factory = factory;
        }

        public ValueTask<ProductionAdmissionFacts> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_factory?.Invoke() ?? _fixture.Facts());
        }
    }
}
