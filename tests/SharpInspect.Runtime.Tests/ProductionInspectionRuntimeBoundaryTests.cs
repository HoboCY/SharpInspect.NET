using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V142_R03_PayloadTransportFaultAfterDurableCoreDisarmsWithoutRepublish()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = true;
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();

        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready,
            "Production did not become ready before the transport-fault cycle");

        peer.RaiseTrigger(61, 1);
        await peer.WaitForPayloadWriteAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var committed = await WaitForProductionCoreAsync(harness);
        var inspectionId = committed.InspectionId;
        Assert.NotNull(committed.Core);
        Assert.Equal((uint)1, committed.Admission.ControllerCycle.CycleSequence);

        try
        {
            // The payload request is held after the Core transaction. Closing the
            // controller connection therefore exercises the real post-Core fault
            // path without manufacturing a result or a second admission.
            peer.Disconnect();
        }
        finally
        {
            peer.ReleasePayloadWrite();
        }

        await WaitProductionAsync(harness, state => state.ArmState == ProductionArmState.Disarmed &&
            !state.Ready && state.Recovery == RecoveryState.Required,
            "Production did not disarm after the post-Core transport fault");
        Assert.False(peer.RuntimeResultValid);
        Assert.Equal(0, peer.ResultValidHighCount);

        var history = await WaitForProductionHistoryAsync(harness, page =>
            page.Events.Any(value => value.Kind == ProductionInspectionEventKind.FaultTerminated),
            "Production fault history was not durable");
        var admissions = history.Events.Where(value => value.Kind == ProductionInspectionEventKind.Admitted)
            .ToArray();
        Assert.Single(admissions);
        Assert.Equal(inspectionId, admissions[0].InspectionId);
        var cores = history.Events.Where(value => value.Kind == ProductionInspectionEventKind.CoreCommitted)
            .ToArray();
        var durableCore = Assert.Single(cores);
        Assert.Equal(inspectionId, durableCore.InspectionId);
        Assert.Contains(history.Events, value => value.Kind == ProductionInspectionEventKind.FaultTerminated);
        Assert.DoesNotContain(history.Events, value =>
            value.Kind == ProductionInspectionEventKind.ResultValidRaised);
        Assert.Equal(inspectionId, durableCore.Core!.Admission.InspectionId);
    }

    [Fact]
    public async Task V142_R04_ExtraAwaitAckTriggerDoesNotCreateSecondInspectionOrCore()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        peer.AutoAcknowledge = false;
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();

        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready,
            "Production did not become ready before the await-ACK cycle");

        peer.RaiseTrigger(61, 1);
        await WaitConditionAsync(() => peer.ResultValidHighCount == 1,
            "Production did not enter the result-ACK window");
        Assert.True(peer.RuntimeResultValid);
        var first = await WaitForProductionCoreAsync(harness);

        // Keep the first result valid while presenting a new controller key. The
        // observer must reject that edge while AwaitAckHigh; it must not allocate
        // a new InspectionId or append another Core.
        var secondKeySample = peer.WaitForControllerSampleAsync(61, 2);
        peer.RaiseTrigger(61, 2);
        await secondKeySample.WaitAsync(TimeSpan.FromSeconds(10));

        // Restore the original key so the already accepted cycle can finish its
        // real ACK handshake. This does not replay the trigger: the trigger edge
        // remains high and the observer has already sampled the rejected key.
        peer.SetControllerCycle(61, 1);
        await peer.WaitForControllerSampleAsync(61, 1).WaitAsync(TimeSpan.FromSeconds(10));
        peer.AcknowledgeResult();
        await WaitConditionAsync(() => peer.AckHighCount == 1,
            "Production did not observe the ACK for the original cycle");
        peer.SetTrigger(false);
        await WaitConditionAsync(() => peer.AckLowCount == 1,
            "Production did not clear the original ACK");
        await WaitProductionAsync(harness, state => state.CurrentExecution is null &&
            !state.Busy && state.Ready,
            "Production did not return to ready after the original ACK");

        var history = await WaitForProductionHistoryAsync(harness, page =>
            page.Events.Any(value => value.Kind == ProductionInspectionEventKind.AcknowledgementReset),
            "Production ACK reset history was not durable");
        var admissions = history.Events.Where(value => value.Kind == ProductionInspectionEventKind.Admitted)
            .ToArray();
        Assert.Single(admissions);
        Assert.Equal(first.InspectionId, admissions[0].InspectionId);
        var cores = history.Events.Where(value => value.Kind == ProductionInspectionEventKind.CoreCommitted)
            .ToArray();
        var durableCore = Assert.Single(cores);
        Assert.Equal(first.InspectionId, durableCore.InspectionId);
        Assert.Equal(first.Core!.ContentHash, durableCore.Core!.ContentHash);
        Assert.Equal(1, peer.ResultValidHighCount);
        Assert.DoesNotContain(history.Events, value =>
            value.Kind == ProductionInspectionEventKind.FaultTerminated);
    }

    private static async Task<ProductionInspectionHistoryEvent> WaitForProductionCoreAsync(
        ManualHarness harness)
    {
        var query = new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        ProductionInspectionHistoryPage? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await query.QueryAsync(new(PageSize: 128));
            if (last.Available)
            {
                var core = last.Events.LastOrDefault(value =>
                    value.Kind == ProductionInspectionEventKind.CoreCommitted && value.Core is not null);
                if (core is not null) return core;
            }
            await Task.Delay(20);
        }

        throw new XunitException("Production Core was not durable: " + last?.ReasonCode);
    }

    private static async Task<ProductionInspectionHistoryPage> WaitForProductionHistoryAsync(
        ManualHarness harness, Func<ProductionInspectionHistoryPage, bool> predicate,
        string timeoutReason)
    {
        var query = new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        ProductionInspectionHistoryPage? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await query.QueryAsync(new(PageSize: 128));
            if (last.Available && predicate(last)) return last;
            await Task.Delay(20);
        }

        throw new XunitException(timeoutReason + ": " + last?.ReasonCode);
    }
}
