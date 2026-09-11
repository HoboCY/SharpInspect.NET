using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData("Acquisition")]
    [InlineData("Payload")]
    [InlineData("AckHigh")]
    [InlineData("AckLow")]
    [InlineData("Timeout")]
    public async Task V145_A02_AbortPreservesFixedOutcomeAndAlarmResetCannotReleaseTheHandshake(string stage)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = stage is "Payload" or "Timeout";
        peer.AutoAcknowledge = stage == "AckLow";
        peer.AutoClearAcknowledge = stage != "AckLow";
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer, productionTestAlarm: ProductionStopTestAlarm(ProductionImpact.FaultAbort),
            // Holding a TCP response deliberately blocks the single transport gate. Give
            // this fixture time to persist the alarm before releasing that response.
            productionCommunicationPolicy: new PlcCommunicationPolicy("V145.AlarmStages", "1",
                TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20),
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
                TimeSpan.FromMilliseconds(40), 2, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(4)));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Ready before staged Abort");
        var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
        runtime.RegisterAlarmSource("Runtime.V145ProductionCondition");
        if (stage == "Acquisition") harness.PauseClock();
        if (stage == "Timeout") harness.Factory.HoldExecution();
        try
        {
            peer.RaiseTrigger(61, 1);
            if (stage == "Acquisition")
                await WaitForProductionHistoryAsync(harness, page => page.Events.Any(value =>
                    value.Kind == ProductionInspectionEventKind.Admitted), "Durable admission before acquisition Abort");
            else if (stage is "Payload" or "Timeout")
                await peer.WaitForPayloadWriteAsync().WaitAsync(TimeSpan.FromSeconds(10));
            else
                await WaitProductionAsync(harness, state => state.Handshake == (stage == "AckHigh"
                    ? HandshakePhase.AwaitingResultAck : HandshakePhase.AwaitingAckReset), "Fixed outcome before Abort");
            var prior = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
            var fixedHash = prior.Latest?.Core?.ContentHash;
            await ObserveProductionStopAlarmAsync(harness, 1, healthy: false);
            peer.ReleasePayloadWrite();
            harness.Factory.ReleaseExecution();
            // Repeated observations keep the same cycle cancellation and immutable Core.
            await ObserveProductionStopAlarmAsync(harness, 2, healthy: false);
            harness.ResumeClock();
            harness.Factory.ReleaseExecution();
            peer.ReleasePayloadWrite();
            var page = await WaitForProductionHistoryAsync(harness, result => result.Events.Any(value =>
                value.Kind == ProductionInspectionEventKind.CoreCommitted), "Abort Core");
            var core = Assert.Single(page.Events, value => value.Kind == ProductionInspectionEventKind.CoreCommitted).Core!;
            Assert.Equal(stage == "Acquisition" ? ExecutionStatus.Cancelled : stage == "Timeout"
                ? ExecutionStatus.Timeout : ExecutionStatus.Success, core.ExecutionStatus);
            if (fixedHash is not null) Assert.Equal(fixedHash, core.ContentHash);
            if (stage != "AckLow") await peer.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await AcknowledgeAndResetProductionStopAlarmAsync(harness, 3);
            var resetState = await runtime.GetSnapshotAsync();
            Assert.False(resetState.Ready);
            Assert.Equal(ProductionArmState.Disarmed, resetState.ArmState);
            Assert.NotNull(resetState.CurrentExecution);
            Assert.Equal(stage != "AckLow", peer.RuntimeResultValid);
            Assert.Equal(stage == "AckLow", peer.ControllerResultAck);
            if (stage == "AckLow")
            {
                peer.AutoClearAcknowledge = true;
                peer.ResetResultAcknowledgement();
            }
            else peer.AcknowledgeResult();
            peer.SetTrigger(false);
            await WaitProductionAsync(harness, state => state.CurrentExecution is null, "Aborted cycle retirement");
            var cold = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
            Assert.False(cold.RecoveryRequired);
            Assert.Equal(core.ContentHash, cold.Latest!.Core!.ContentHash);
            Assert.Equal(1, peer.ResultValidHighCount);
            if (stage == "Timeout")
            {
                // Timeout retires the exact prepared instance. Alarm reset cannot repair
                // that independent preparation/qualification gate.
                var arm = await runtime.SubmitAsync(new ArmProductionCommand(Guid.NewGuid(), harness.Invocation()));
                Assert.Equal(CommandDisposition.Rejected, arm.Disposition);
                Assert.False((await runtime.GetSnapshotAsync()).Ready);
            }
            else
            {
                await ArmProductionAsync(harness);
                await WaitProductionAsync(harness, state => state.Ready, "Independent Manual Arm after alarm reset");
            }
        }
        finally
        {
            harness.ResumeClock();
            harness.Factory.ReleaseExecution();
            peer.ReleasePayloadWrite();
        }
    }

    [Theory]
    [InlineData(ProductionImpact.None)]
    [InlineData(ProductionImpact.BlockNewTriggers)]
    public async Task V145_A03_CriticalSeverityWithoutFaultAbortDoesNotCancelAcceptedComputation(ProductionImpact impact)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            productionTestAlarm: ProductionStopTestAlarm(impact) with { Severity = AlarmSeverity.Critical });
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Ready before non-aborting alarm");
        Assert.IsType<StationRuntime>(harness.Runtime).RegisterAlarmSource("Runtime.V145ProductionCondition");
        harness.Factory.HoldExecution(cooperativeCancellation: true);
        try
        {
            peer.RaiseTrigger(61, 1);
            await harness.Factory.ExecutionEntered.WaitAsync(TimeSpan.FromSeconds(10));
            await ObserveProductionStopAlarmAsync(harness, 1, healthy: false);
            harness.Factory.ReleaseExecution();
            peer.SetTrigger(false);
            var page = await WaitForProductionHistoryAsync(harness, result => result.Events.Any(value =>
                value.Kind == ProductionInspectionEventKind.AcknowledgementReset), "Non-aborting alarm delivery");
            Assert.Equal(ExecutionStatus.Success, Assert.Single(page.Events, value =>
                value.Kind == ProductionInspectionEventKind.CoreCommitted).Core!.ExecutionStatus);
            Assert.DoesNotContain(page.Events, value => value.Kind == ProductionInspectionEventKind.FaultTerminated);
            if (impact == ProductionImpact.BlockNewTriggers)
                Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
        }
        finally { harness.Factory.ReleaseExecution(); }
    }

    private static async Task ObserveProductionStopAlarmAsync(ManualHarness harness, long sequence, bool healthy)
    {
        var state = await harness.Runtime.GetSnapshotAsync();
        var result = await Assert.IsType<StationRuntime>(harness.Runtime).ObserveAlarmAsync(new AlarmObservation(
            state.RuntimeEpoch, sequence, "V145ProductionCondition", "Runtime.V145ProductionCondition", healthy,
            DateTimeOffset.UtcNow));
        Assert.True(result.Accepted, result.ReasonCode);
    }

    private static async Task AcknowledgeAndResetProductionStopAlarmAsync(ManualHarness harness, long sequence)
    {
        var state = await harness.Runtime.GetSnapshotAsync();
        var alarm = Assert.Single(state.AlarmState!.Instances, value => value.Code == "V145ProductionCondition");
        var ack = await harness.Runtime.SubmitAsync(new AcknowledgeAlarmCommand(Guid.NewGuid(), harness.Invocation(), alarm.InstanceId));
        Assert.Equal(CommandDisposition.Accepted, ack.Disposition);
        await ObserveProductionStopAlarmAsync(harness, sequence, healthy: true);
        var correlation = Guid.NewGuid();
        var grant = await harness.Service<IStepUpAuthentication>().ReauthenticateAsync(new StepUpRequest(correlation,
            harness.Invocation(), new StepUpBinding(Permission.ResetAlarm, correlation, alarm.InstanceId.ToString("D"),
                AuditedCommandKind.ResetAlarm), harness.Fixture.Password));
        Assert.True(grant.Succeeded, grant.ReasonCode);
        var reset = await harness.Runtime.SubmitAsync(new ResetAlarmCommand(correlation,
            harness.Invocation() with { StepUpGrantId = grant.GrantId }, alarm.InstanceId));
        Assert.Equal(CommandDisposition.Accepted, reset.Disposition);
    }
}
