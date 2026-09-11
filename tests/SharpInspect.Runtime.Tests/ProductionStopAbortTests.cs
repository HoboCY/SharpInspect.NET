using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData("LoggedOut")]
    [InlineData("Locked")]
    [InlineData("StaleInvocation")]
    public async Task V145_S02_PhysicalLocalStopSurvivesSessionLossWithoutGrantingArm(string sessionState)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Ready before session loss");
        var oldInvocation = harness.Invocation();
        var session = sessionState == "Locked"
            ? await harness.Fixture.Sessions.LockAsync(oldInvocation.SessionId, SessionLockReason.UserRequested)
            : await harness.Fixture.Sessions.LogoutAsync(oldInvocation.SessionId);
        Assert.True(session.Succeeded, session.ReasonCode);
        var invocation = sessionState == "StaleInvocation" ? oldInvocation : new CommandInvocation(CommandSource.PhysicalConsole);
        var correlation = Guid.NewGuid();
        var stop = await harness.Runtime.SubmitAsync(new GracefulProductionStopCommand(correlation, invocation));
        Assert.Equal(CommandDisposition.Accepted, stop.Disposition);
        Assert.Equal(AuditPersistence.Persisted, stop.Audit);
        Assert.Equal(correlation, stop.CorrelationId);
        await WaitProductionAsync(harness, state => state.LastCommand is { State: OperationState.Completed } result &&
            result.CorrelationId == correlation, "Unauthenticated physical Stop completion");
        var arm = await harness.Runtime.SubmitAsync(new ArmProductionCommand(Guid.NewGuid(), invocation));
        Assert.Equal(CommandDisposition.Rejected, arm.Disposition);
        Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
        Assert.Equal(0, peer.ResultValidHighCount);
    }

    [Fact]
    public async Task V145_A04_StopRetainsItsCycleOutcomeWhenCompletionWorkerStartsAfterAbortRetirement()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer, productionTestAlarm: ProductionStopTestAlarm(ProductionImpact.FaultAbort),
            heartbeatInterval: TimeSpan.FromSeconds(1));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Ready before delayed Stop worker");
        var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
        runtime.RegisterAlarmSource("Runtime.V145ProductionCondition");
        harness.Factory.HoldExecution(cooperativeCancellation: true);
        var completionField = typeof(StationRuntime).GetField("_completion",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var sync = typeof(StationRuntime).GetField("_sync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(runtime)!;
        try
        {
            peer.RaiseTrigger(61, 1);
            await harness.Factory.ExecutionEntered.WaitAsync(TimeSpan.FromSeconds(10));
            var stopId = Guid.NewGuid();
            Assert.Equal(CommandDisposition.Accepted, (await runtime.SubmitAsync(new GracefulProductionStopCommand(
                stopId, new(CommandSource.PhysicalConsole)))).Disposition);
            lock (sync)
            {
                Assert.Null(completionField.GetValue(runtime));
                // Deterministically hold only worker scheduling, without holding the
                // runtime/command lock needed by the real alarm and protocol paths.
                completionField.SetValue(runtime, Task.CompletedTask);
            }
            await ObserveProductionStopAlarmAsync(harness, 1, healthy: false);
            peer.SetTrigger(false);
            await WaitProductionAsync(harness, state => state.CurrentExecution is null, "Abort before Stop worker starts");
            lock (sync) completionField.SetValue(runtime, null);
            await WaitProductionAsync(harness, state => state.LastCommand is { State: not OperationState.Pending },
                "Delayed Stop must retain its interrupted cycle result");
            var terminal = (await runtime.GetSnapshotAsync()).LastCommand!;
            Assert.Equal(stopId, terminal.CorrelationId);
            Assert.Equal(OperationState.Failed, terminal.State);
            Assert.Equal("ProductionStopInterrupted", terminal.ReasonCode);
            // An independent later idle Stop must not inherit the previous cycle's failure.
            var idleId = Guid.NewGuid();
            Assert.Equal(CommandDisposition.Accepted, (await runtime.SubmitAsync(new GracefulProductionStopCommand(
                idleId, new(CommandSource.PhysicalConsole)))).Disposition);
            await WaitProductionAsync(harness, state => state.LastCommand is { State: OperationState.Completed } command &&
                command.CorrelationId == idleId, "Idle Stop must not inherit an old cycle outcome");
        }
        finally
        {
            harness.Factory.ReleaseExecution();
            lock (sync) if (ReferenceEquals(completionField.GetValue(runtime), Task.CompletedTask))
                completionField.SetValue(runtime, null);
        }
    }

    private static AlarmPolicyRule ProductionStopTestAlarm(ProductionImpact impact) =>
        new("V145ProductionCondition", "Runtime.V145ProductionCondition", AlarmSeverity.Warning,
            impact, true, AlarmNotification.UntilCleared, null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V145_A01_FaultAbortDuringAlgorithmDeliversCancelledUnknownThroughHealthyPlc(bool stopPending)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer, productionTestAlarm: ProductionStopTestAlarm(ProductionImpact.FaultAbort));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Ready before alarm Fault Abort");
        var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
        runtime.RegisterAlarmSource("Runtime.V145ProductionCondition");
        harness.Factory.HoldExecution(cooperativeCancellation: true);
        Guid? stopCorrelation = null;
        try
        {
            peer.RaiseTrigger(61, 1);
            await harness.Factory.ExecutionEntered.WaitAsync(TimeSpan.FromSeconds(10));
            if (stopPending)
            {
                stopCorrelation = Guid.NewGuid();
                var stop = await runtime.SubmitAsync(new GracefulProductionStopCommand(stopCorrelation.Value,
                    new CommandInvocation(CommandSource.PhysicalConsole)));
                Assert.Equal(CommandDisposition.Accepted, stop.Disposition);
            }
            var before = await runtime.GetSnapshotAsync();
            var observed = await runtime.ObserveAlarmAsync(new AlarmObservation(before.RuntimeEpoch, 1,
                "V145ProductionCondition", "Runtime.V145ProductionCondition", false, DateTimeOffset.UtcNow));
            Assert.True(observed.Accepted, observed.ReasonCode);
            var history = await WaitForProductionHistoryAsync(harness, page => page.Events.Any(value =>
                value.Kind is ProductionInspectionEventKind.CoreCommitted or ProductionInspectionEventKind.FaultTerminated),
                "Fault Abort must reach a durable result or fault boundary");
            var core = Assert.Single(history.Events, value => value.Kind == ProductionInspectionEventKind.CoreCommitted).Core!;
            Assert.Equal(ExecutionStatus.Cancelled, core.ExecutionStatus);
            Assert.Equal(InspectionDecision.Unknown, core.Decision);
            Assert.Null(core.Result);
            await peer.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(10));
            peer.SetTrigger(false);
            await peer.WaitForAckLowAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitProductionAsync(harness, state => state.CurrentExecution is null,
                "Cancelled production cycle must retire after acknowledgement");
            var after = await runtime.GetSnapshotAsync();
            Assert.False(after.Ready);
            Assert.Equal(ProductionArmState.Disarmed, after.ArmState);
            var cold = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
            Assert.True(cold.Available, cold.ReasonCode);
            Assert.False(cold.RecoveryRequired);
            Assert.Equal(core.ContentHash, cold.Latest!.Core!.ContentHash);
            Assert.Equal(1, peer.ResultValidHighCount);
            if (stopCorrelation is { } stopId)
            {
                await WaitProductionAsync(harness, state => state.LastCommand is { State: not OperationState.Pending },
                    "Interrupted Stop must reach a truthful terminal outcome");
                var stopped = (await runtime.GetSnapshotAsync()).LastCommand!;
                Assert.Equal(stopId, stopped.CorrelationId);
                Assert.Equal(OperationState.Failed, stopped.State);
                Assert.Equal("ProductionStopInterrupted", stopped.ReasonCode);
            }
        }
        finally { harness.Factory.ReleaseExecution(); }
    }

    [Theory]
    [InlineData("Acquisition")]
    [InlineData("Execution")]
    [InlineData("Payload")]
    [InlineData("AckHigh")]
    [InlineData("AckLow")]
    public async Task V145_S01_GracefulStopPreservesCurrentCycleAndRequiresSeparateArm(string stage)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = stage == "Payload";
        peer.AutoAcknowledge = stage == "AckLow";
        peer.AutoClearAcknowledge = stage != "AckLow";
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Ready before staged Graceful Stop");
        if (stage == "Acquisition") harness.PauseClock();
        if (stage == "Execution") harness.Factory.HoldExecution();
        try
        {
            peer.RaiseTrigger(61, 1);
            if (stage == "Acquisition")
                await WaitForProductionHistoryAsync(harness,
                    page => page.Events.Any(value => value.Kind == ProductionInspectionEventKind.Admitted),
                    "Durably accepted acquisition");
            else if (stage == "Execution")
                await harness.Factory.ExecutionEntered.WaitAsync(TimeSpan.FromSeconds(10));
            else if (stage == "Payload")
                await peer.WaitForPayloadWriteAsync().WaitAsync(TimeSpan.FromSeconds(10));
            else if (stage == "AckHigh")
                await WaitProductionAsync(harness, state => state.Handshake == HandshakePhase.AwaitingResultAck, "AwaitAckHigh");
            else
                await WaitProductionAsync(harness, state => state.Handshake == HandshakePhase.AwaitingAckReset, "AwaitAckLow");

            var stop = await harness.Runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(),
                new CommandInvocation(CommandSource.PhysicalConsole)));
            Assert.Equal(CommandDisposition.Accepted, stop.Disposition);
            Assert.Equal(AuditPersistence.Persisted, stop.Audit);
            var stopped = await harness.Runtime.GetSnapshotAsync();
            Assert.False(stopped.Ready);
            Assert.Equal(ProductionArmState.Disarmed, stopped.ArmState);
            Assert.Equal(OperationState.Pending, stopped.LastCommand?.State);
            if (stage is "AckHigh" or "AckLow")
            {
                var duplicate = await harness.Runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(),
                    new CommandInvocation(CommandSource.PhysicalConsole)));
                Assert.Equal(CommandDisposition.Rejected, duplicate.Disposition);
                Assert.Equal(stop.CorrelationId, (await harness.Runtime.GetSnapshotAsync()).LastCommand?.CorrelationId);
                Assert.Equal(stage == "AckHigh", peer.RuntimeResultValid);
                Assert.Equal(stage == "AckLow", peer.ControllerResultAck);
            }
            harness.ResumeClock();
            harness.Factory.ReleaseExecution();
            peer.ReleasePayloadWrite();
            if (stage != "AckLow")
            {
                await peer.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(10));
                peer.AcknowledgeResult();
            }
            else
            {
                peer.AutoClearAcknowledge = true;
                peer.ResetResultAcknowledgement();
            }
            peer.SetTrigger(false);
            await peer.WaitForAckLowAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitProductionAsync(harness, state => state.LastCommand is
                { State: OperationState.Completed, ReasonCode: "LocallyDisarmed" }, "Graceful Stop completion");
            var cold = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).QueryAsync(new(PageSize: 128));
            Assert.True(cold.Available, cold.ReasonCode);
            Assert.Single(cold.Events, value => value.Kind == ProductionInspectionEventKind.Admitted);
            Assert.Single(cold.Events, value => value.Kind == ProductionInspectionEventKind.CoreCommitted);
            Assert.Contains(cold.Events, value => value.Kind == ProductionInspectionEventKind.AcknowledgementReset);
            Assert.DoesNotContain(cold.Events, value => value.Kind == ProductionInspectionEventKind.FaultTerminated);
            Assert.Equal(ExecutionStatus.Success, cold.Events.Single(value =>
                value.Kind == ProductionInspectionEventKind.CoreCommitted).Core!.ExecutionStatus);
            Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
            await ArmProductionAsync(harness);
            await WaitProductionAsync(harness, state => state.Ready, "Explicit Manual Arm after completed Stop");
        }
        finally
        {
            harness.ResumeClock();
            harness.Factory.ReleaseExecution();
            peer.ReleasePayloadWrite();
        }
    }
}
