using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V145_R02_PersistentRecoveryFenceContentionUsesOneDeadlineAndLeavesGrantUnconsumed()
    {
        await using var scenario = await ProductionRecoveryScenario.CreateAsync(
            coldBoundary: ProductionInspectionEventKind.CoreCommitted);
        var command = await scenario.AuthorizeAsync(await scenario.CreateCommandAsync("PersistentStopRecoveryFence"));
        var evidence = await CaptureRecoveryRetryEvidenceAsync(scenario);
        var requests = scenario.Peer.RequestCount;
        var calls = 0;
        var budget = scenario.Harness.Fixture.Options.CommitTimeout;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var denied = await scenario.RuntimeContext.Authorization.AuthorizeProductionRecoveryAsync(command,
            evidence.RuntimeEpoch, evidence.Capture, evidence.Observation, null,
            () => { Interlocked.Increment(ref calls); return "ProductionRecoveryCommitFenceBusy"; })
            .AsTask().WaitAsync(budget + TimeSpan.FromSeconds(2));
        Assert.Equal(CommandDisposition.Rejected, denied.Outcome.Disposition);
        Assert.Equal(AuditPersistence.Unavailable, denied.Outcome.Audit);
        // The same deadline also bounds the audit verification inside each
        // transaction attempt, so it may expire before the next fence check.
        Assert.Contains(denied.Outcome.ReasonCode,
            new[] { "ProductionRecoveryCommitFenceBusy", "IdentityCommitDeadlineExceeded",
                "AuditVerificationDeadlineExceeded" });
        Assert.True(calls > 1);
        Assert.InRange(elapsed.Elapsed.TotalMilliseconds, budget.TotalMilliseconds * .7,
            budget.TotalMilliseconds + 1000);
        Assert.Equal(requests, scenario.Peer.RequestCount);
        var audit = await new SqliteCommandTraceQuery(scenario.Harness.Fixture.Options)
            .QueryAsync(new(CorrelationId: command.CorrelationId));
        Assert.Empty(audit.Records);
        var history = await new SqliteProductionInspectionHistoryQuery(scenario.Harness.Fixture.Options)
            .QueryAsync(new(InspectionId: scenario.InspectionId));
        Assert.True(history.Available, history.ReasonCode);
        Assert.DoesNotContain(history.Events, value => value.Kind == ProductionInspectionEventKind.RecoveryRequired);

        // No durable authorization or one-time grant was consumed by the abandoned
        // transaction attempts. A later healthy attempt can use the same exact grant.
        // If the shared deadline expired during verification, wait for the real
        // background verifier to restore health before making that later attempt.
        using (var verification = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (scenario.Harness.Fixture.Store.Integrity?.State != AuditIntegrityState.Verified)
                await Task.Delay(25, verification.Token);
        var accepted = await scenario.RuntimeContext.Authorization.AuthorizeProductionRecoveryAsync(command,
            evidence.RuntimeEpoch, evidence.Capture, evidence.Observation, null, () => null);
        Assert.True(accepted.Outcome.Disposition == CommandDisposition.Accepted, accepted.Outcome.ReasonCode);
        Assert.Equal(requests, scenario.Peer.RequestCount);
    }

    [Fact]
    public async Task V145_R03_RecoveryRetryHonorsRevocationInsteadOfRetryingItAsContention()
    {
        await using var scenario = await ProductionRecoveryScenario.CreateAsync(
            coldBoundary: ProductionInspectionEventKind.CoreCommitted);
        var command = await scenario.AuthorizeAsync(await scenario.CreateCommandAsync("RevokedStopRecoveryFence"));
        var evidence = await CaptureRecoveryRetryEvidenceAsync(scenario);
        var requests = scenario.Peer.RequestCount;
        var calls = 0;
        var denied = await scenario.RuntimeContext.Authorization.AuthorizeProductionRecoveryAsync(command,
            evidence.RuntimeEpoch, evidence.Capture, evidence.Observation, null,
            () => Interlocked.Increment(ref calls) == 1 ? "ProductionRecoveryCommitFenceBusy" : "ProductionRecoveryCancelled");
        Assert.Equal(CommandDisposition.Rejected, denied.Outcome.Disposition);
        Assert.Equal(AuditPersistence.Persisted, denied.Outcome.Audit);
        Assert.Equal("ProductionRecoveryCancelled", denied.Outcome.ReasonCode);
        Assert.Equal(2, calls);
        Assert.Equal(requests, scenario.Peer.RequestCount);
        var audit = await new SqliteCommandTraceQuery(scenario.Harness.Fixture.Options)
            .QueryAsync(new(CorrelationId: command.CorrelationId));
        Assert.Equal(CommandDisposition.Rejected, Assert.Single(audit.Records).Disposition);
        var history = await new SqliteProductionInspectionHistoryQuery(scenario.Harness.Fixture.Options)
            .QueryAsync(new(InspectionId: scenario.InspectionId));
        Assert.True(history.Available, history.ReasonCode);
        Assert.DoesNotContain(history.Events, value => value.Kind == ProductionInspectionEventKind.RecoveryRequired);
    }

    private static async Task<(Guid RuntimeEpoch, ProductionRecoverySafetyCapture Capture,
        ProductionRecoveryObservation Observation)> CaptureRecoveryRetryEvidenceAsync(ProductionRecoveryScenario scenario)
    {
        var snapshot = await scenario.Runtime.GetSnapshotAsync();
        var registry = Assert.IsType<ProductionRecoverySafetyRegistry>(typeof(StationRuntime).GetField(
            "_productionRecoverySafety", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(scenario.Runtime));
        var capture = await registry.CaptureAsync(snapshot.RuntimeEpoch);
        Assert.True(capture.Available, capture.ReasonCode);
        var history = await new SqliteProductionInspectionHistoryQuery(scenario.Harness.Fixture.Options)
            .QueryAsync(new(InspectionId: scenario.InspectionId));
        Assert.True(history.Available, history.ReasonCode);
        var core = Assert.IsType<ProductionInspectionCore>(Assert.Single(history.Events,
            value => value.Kind == ProductionInspectionEventKind.CoreCommitted).Core);
        var admission = core.Admission;
        return (snapshot.RuntimeEpoch, capture, new ProductionRecoveryObservation(ProductionRecoveryDeliveryPhase.CoreCommitted,
            ProductionRecoveryUncertaintyKind.ProcessRestart, ProductionRecoveryAcknowledgementObservation.Unknown,
            admission.RuntimeEpoch, admission.ControllerCycle.ControllerEpoch, admission.ControllerCycle.CycleSequence,
            admission.ConnectionGeneration, admission.EndpointBindingHash, admission.PlcProfileHash,
            admission.PlcPolicyHash, core.ContentHash, core.PlcPayload?.ContentHash, core.PlcPayload?.WireContentHash));
    }
}
