using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Cycles;
using System.Reflection;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V141_R08_InitialEpochJitterCanSynchronizeWithoutInventingColdRecovery()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.FreezeControllerHeartbeat = true;
        await using var harness = await QualificationHarness.CreateAsync(
            profileFactory: snapshot => controller.CreateCommunicationProfile(snapshot), enablePlcCommunication: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "initial epoch observation start");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await controller.WaitForControllerLowObservedAsync(deadline.Token);
        controller.SetControllerCycle(62, 1);
        controller.FreezeControllerHeartbeat = false;
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var session = await harness.WaitForSnapshotAsync(value => value.SessionId is not null, "initial epoch session");
        AssertAccepted(await harness.Runtime.SubmitAsync(harness.ExitCommand(session.SessionId!.Value, false)), "initial epoch exit");
        await harness.WaitForSnapshotAsync(value => value.Phase == StationQualificationSessionPhase.Closed, "initial epoch closed");
        await using var cold = await harness.OpenColdScopeAsync();
        // A fresh read-only connection verifies the durable communication facts.
        var history = await new SqlitePlcCommunicationHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(QualificationSessionId: session.SessionId, PageSize: 128));
        Assert.True(history.Available, history.ReasonCode);
        Assert.False(history.RecoveryRequired);
        Assert.Contains(history.Events, value => value.Kind == PlcCommunicationEventKind.ControllerEpochObserved && value.ControllerEpoch == 62);
        Assert.Contains(history.Events, value => value.Kind == PlcCommunicationEventKind.SynchronizationWindowObserved);
        Assert.DoesNotContain(history.Events, value => value.Kind == PlcCommunicationEventKind.RecoveryCompleted);
        Assert.Equal(0, cold.Facility.OpenCount);
        Assert.Equal(1, controller.ReadyWriteCount);
    }

    [Fact]
    public async Task V141_R07_ColdPendingDeliveryHasNewRuntimeIdentityAndPerformsZeroPlcIo()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.AutoAcknowledge = false;
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(maximumRuns: 1,
            allowDisposeFailure: true, profileFactory: snapshot => controller.CreateCommunicationProfile(snapshot),
            enablePlcCommunication: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "cold communication start");
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var runtimeEpoch = (await harness.Runtime.GetSnapshotAsync()).RuntimeEpoch;
        Assert.Equal(runtimeEpoch, controller.ReceivedRuntimeEpoch);
        controller.RaiseTrigger(61, 1);
        await controller.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var session = await harness.WaitForSnapshotAsync(value => value.SessionId is not null, "cold communication session");
        controller.SetControllerCycle(62, 1);
        var before = await harness.WaitForHistoryAsync(session.SessionId!.Value, page => page.Events.Any(value =>
            value.ReasonCode == "QualificationModbusRecoveryRequired"), "cold communication retirement");
        var original = Assert.Single(before.Runs);
        Assert.NotNull(original.QualificationPayload);
        var requests = controller.RequestCount;
        var connections = controller.ConnectionCount;
        await using var cold = await harness.OpenColdScopeAsync();
        await cold.WaitForSnapshotAsync(value => value.Phase == StationQualificationSessionPhase.RecoveryBlocked,
            "cold communication recovery interlock");
        Assert.NotEqual(runtimeEpoch, (await cold.Runtime.GetSnapshotAsync()).RuntimeEpoch);
        Assert.Equal(0, cold.Facility.OpenCount);
        Assert.Equal(0, cold.Facility.RestoreCount);
        Assert.Equal(requests, controller.RequestCount);
        Assert.Equal(connections, controller.ConnectionCount);
        var after = await cold.History.QueryAsync(new(SessionId: session.SessionId, PageSize: 128));
        Assert.True(after.Available, after.ReasonCode);
        Assert.Equal(original.QualificationPayload!.ContentHash, Assert.Single(after.Runs).QualificationPayload!.ContentHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V141_R06_ExitDuringBootstrapRevokesPollingAndReadyQueuedAtTransport(bool abort)
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.FreezeControllerHeartbeat = true;
        await using var harness = await QualificationHarness.CreateAsync(allowDisposeFailure: true,
            profileFactory: snapshot => controller.CreateCommunicationProfile(snapshot), enablePlcCommunication: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "bootstrap exit start");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (controller.ReceivedRuntimeEpoch == Guid.Empty) await Task.Delay(5, deadline.Token);
        var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
        var state = await runtime.GetSnapshotAsync();
        Assert.Equal(state.RuntimeEpoch, controller.ReceivedRuntimeEpoch);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var owner = typeof(StationRuntime).GetField("_stationQualificationOwner", hidden)!.GetValue(runtime)!;
        var startRequest = typeof(StationRuntime).GetMethod("StartQualificationModbusRequest", hidden)!;
        // Exercise the same actual transport send gate with the live Runtime
        // owner. The second peer isolates the queued-write observation from
        // bootstrap's already admitted network request.
        await using var sendPeer = ModbusQualificationTestServer.Start();
        var sendProfile = sendPeer.CreateCommunicationProfile();
        await using var channel = new ModbusQualificationChannel(sendProfile, startOwnedRequest: start =>
        {
            try { return (Task)startRequest.Invoke(runtime, new[] { owner, start })!; }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
        });
        await channel.ConnectAsync();
        var transportGate = (SemaphoreSlim)typeof(ModbusQualificationChannel).GetField("_transportGate", hidden)!.GetValue(channel)!;
        await transportGate.WaitAsync();
        var output = new InspectionCycleOutputLatch(channel.WriteStateAsync);
        var queuedReady = output.ChangeAsync(CancellationToken.None, ready: true);
        try
        {
            var session = await harness.WaitForSnapshotAsync(value => value.SessionId is not null, "bootstrap session");
            AssertAccepted(await runtime.SubmitAsync(harness.ExitCommand(session.SessionId!.Value, abort)), "bootstrap exit");
            Assert.False(queuedReady.IsCompleted);
        }
        finally { transportGate.Release(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedReady);
        Assert.Empty(sendPeer.StateWrites);
        await harness.WaitForSnapshotAsync(value => value.Phase == (abort
            ? StationQualificationSessionPhase.RecoveryBlocked : StationQualificationSessionPhase.Closed), "bootstrap retirement");
        // Retirement may write zero Ready, but must never restart the owner.
        await Task.Delay(100);
        var requestCount = controller.RequestCount;
        controller.FreezeControllerHeartbeat = false;
        await Task.Delay(150);
        Assert.Equal(requestCount, controller.RequestCount);
        Assert.Equal(0, controller.ReadyWriteCount);
        Assert.Equal(0, controller.ResultValidHighCount);
        var history = await new SqlitePlcCommunicationHistoryQuery(harness.Fixture.Options).QueryAsync(new(PageSize: 128));
        Assert.True(history.Available, history.ReasonCode);
        Assert.NotEmpty(history.Events);
        Assert.All(history.Events, value => Assert.Equal(state.RuntimeEpoch, value.RuntimeEpoch));
        Assert.DoesNotContain(history.Events, value => value.Kind == PlcCommunicationEventKind.SynchronizationWindowObserved);
    }

    [Fact]
    public async Task V141_R04_CommunicationProfileWithoutItsAuditStoreRejectsBeforeFacilityOrTcpIo()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        await using var harness = await QualificationHarness.CreateAsync(
            profileFactory: snapshot => controller.CreateCommunicationProfile(snapshot));
        var rejected = await harness.StartWithFreshStepUpAsync();
        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
        Assert.Equal("QualificationPlcCommunicationStoreUnavailable", rejected.ReasonCode);
        Assert.Equal(0, controller.ConnectionCount);
        Assert.Equal(0, harness.Facility.OpenCount);
    }

    [Fact]
    public async Task V141_R05_InitialRetainedValidSurvivesRejectionAndBoundedCommunicationRecovery()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.SeedRetainedRuntimeState(false, true, false, false);
        await using var harness = await QualificationHarness.CreateAsync(allowDisposeFailure: true,
            profileFactory: snapshot => controller.CreateCommunicationProfile(snapshot), enablePlcCommunication: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "retained valid start");
        var state = await harness.WaitForSnapshotAsync(value => value.SessionId is not null &&
            value.Phase == StationQualificationSessionPhase.RecoveryBlocked, "retained valid interlock");
        var history = await harness.WaitForHistoryAsync(state.SessionId!.Value, page => page.Events.Any(value =>
            value.ReasonCode == "QualificationModbusRecoveryRequired"), "retained valid retirement");
        Assert.Empty(history.Runs);
        Assert.True(controller.RuntimeResultValid);
        Assert.Empty(controller.StateWrites);
        Assert.Equal(0, controller.ResultValidLowCount);
        Assert.Equal(0, controller.ReadyWriteCount);
    }

    [Theory]
    [InlineData(true, "QualificationControllerEpochChanged")]
    [InlineData(false, "PlcControllerHeartbeatStale")]
    public async Task V141_R01_CommunicationLossBeforeCoreCancelsActualAlgorithmWithOriginalIdentity(
        bool changeEpoch, string reason)
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.HoldFirstPayloadWrite = false;
        var factory = new QualificationWriterRaceFactory();
        await using var harness = await QualificationHarness.CreateAsync(maximumRuns: 1,
            allowDisposeFailure: true, profileFactory: snapshot => controller.CreateCommunicationProfile(snapshot),
            qualificationExecutionFactory: factory, enablePlcCommunication: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "communication pre-core start");
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(15));
        controller.RaiseTrigger(61, 1);
        await factory.ExecutionEntered.WaitAsync(TimeSpan.FromSeconds(15));
        var running = await harness.WaitForSnapshotAsync(value => value.CurrentRunId is not null, "communication run admission");
        var sessionId = Assert.IsType<Guid>(running.SessionId);
        if (changeEpoch) controller.SetControllerCycle(62, 1);
        else controller.FreezeControllerHeartbeat = true;
        var history = await harness.WaitForHistoryAsync(sessionId,
            page => page.Runs.Any(value => value.Terminal), "communication cancellation core");
        factory.ReleaseExecution();
        var run = Assert.Single(history.Runs);
        Assert.Equal(ExecutionStatus.Cancelled, run.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, run.Decision);
        Assert.Equal(reason, run.ReasonCode);
        Assert.Equal((uint)61, run.ControllerEpoch);
        Assert.Equal((uint)1, run.CycleSequence);
        Assert.Null(run.QualificationPayload);
        Assert.Equal(0, controller.ResultValidHighCount);
        var retired = await harness.WaitForHistoryAsync(sessionId, page => page.Events.Any(value =>
            value.ReasonCode == "QualificationModbusRecoveryRequired"), "communication recovery retirement");
        Assert.Single(retired.Runs);
        Assert.Equal(1, controller.ReadyWriteCount);
        var state = await harness.Runtime.GetSnapshotAsync();
        Assert.False(state.Ready);
        Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
        Assert.False(state.PlcCommunication!.Healthy);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task V141_R02_CommunicationLossInEitherAckPhasePreservesCommittedPayload(
        bool awaitingAckLow, bool changeEpoch)
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.HoldFirstPayloadWrite = false;
        controller.AutoAcknowledge = awaitingAckLow;
        controller.AutoClearAcknowledge = false;
        await using var harness = await QualificationHarness.CreateAsync(maximumRuns: 1,
            allowDisposeFailure: true, profileFactory: snapshot => controller.CreateCommunicationProfile(snapshot),
            enablePlcCommunication: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "communication ack-phase start");
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(15));
        controller.RaiseTrigger(61, 1);
        await controller.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var snapshot = await harness.WaitForSnapshotAsync(value => value.SessionId is not null, "communication session");
        var sessionId = Assert.IsType<Guid>(snapshot.SessionId);
        var before = await harness.WaitForHistoryAsync(sessionId, page => page.Runs.Any(value => value.Terminal) &&
            (!awaitingAckLow || !controller.RuntimeResultValid), "communication acknowledged-high stage");
        var original = Assert.Single(before.Runs);
        Assert.Equal(ExecutionStatus.Success, original.ExecutionStatus);
        Assert.NotNull(original.QualificationPayload);
        var payloadWrites = controller.Writes.Count(value => value.StartAddress != harness.ModbusProfile!.RuntimeStartAddress &&
            value.StartAddress != harness.ModbusProfile.CommunicationBinding!.RuntimeStartAddress);
        if (changeEpoch) controller.SetControllerCycle(62, 1);
        else controller.FreezeRuntimeEcho = true;
        await harness.WaitForSnapshotAsync(value => value.Phase == StationQualificationSessionPhase.RecoveryBlocked,
            "communication ack recovery barrier");
        // A late old acknowledgement must not complete the old cycle on a new connection.
        if (!awaitingAckLow) controller.AcknowledgeResult();
        var after = await harness.WaitForHistoryAsync(sessionId, page => page.Events.Any(value =>
            value.ReasonCode == "QualificationModbusRecoveryRequired"), "communication ack recovery retirement");
        var final = Assert.Single(after.Runs);
        Assert.Equal(original.RunId.Value, final.RunId.Value);
        Assert.Equal(original.QualificationPayload!.ContentHash, final.QualificationPayload!.ContentHash);
        Assert.Equal(original.ResultPayloadHash, final.ResultPayloadHash);
        Assert.Equal(ExecutionStatus.Success, final.ExecutionStatus);
        Assert.Equal((uint)61, final.ControllerEpoch);
        Assert.Equal(payloadWrites, controller.Writes.Count(value => value.StartAddress != harness.ModbusProfile!.RuntimeStartAddress &&
            value.StartAddress != harness.ModbusProfile.CommunicationBinding!.RuntimeStartAddress));
        Assert.Equal(1, controller.ReadyWriteCount);
        Assert.Equal(1, controller.ResultValidHighCount);
        Assert.Equal(awaitingAckLow ? 1 : 0, controller.ResultValidLowCount);
        var cycles = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(SessionId: sessionId, PageSize: 128));
        Assert.True(cycles.Available, cycles.ReasonCode);
        Assert.True(cycles.RecoveryRequired);
        Assert.DoesNotContain(cycles.Events, value => value.Kind == QualificationCycleEventKind.AckReset);
        var communication = await new SqlitePlcCommunicationHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(QualificationSessionId: sessionId, PageSize: 128));
        Assert.True(communication.Available, communication.ReasonCode);
        Assert.Contains(communication.Events, value => value.RecoveryCycleId is not null &&
            value.RunId == original.RunId.Value && value.CycleSequence == 1);
        Assert.Equal(0, controller.ProductionReadyWriteCount);
    }

    [Fact]
    public async Task V141_R03_EpochChangeAfterRealCoreCommitNeverRewritesOrPublishesThatCore()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(maximumRuns: 1,
            allowDisposeFailure: true, profileFactory: snapshot => controller.CreateCommunicationProfile(snapshot),
            enablePlcCommunication: true);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Fixture.Store.QualificationCoreCommitGuardDecorator = guard =>
            new DelayedQualificationCommitGuard(guard, entered, release);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "communication committed core start");
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(15));
        // Resolve authorized session identity before the COMMIT guard can
        // acquire its identity lease; otherwise this test waits on itself.
        var snapshot = await harness.WaitForSnapshotAsync(value => value.SessionId is not null, "communication core session");
        var sessionId = Assert.IsType<Guid>(snapshot.SessionId);
        using var observations = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        await using var snapshots = harness.Runtime.WatchSnapshotsAsync(observations.Token).GetAsyncEnumerator();
        Assert.True(await snapshots.MoveNextAsync());
        controller.RaiseTrigger(61, 1);
        string coreHash;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var cycles = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
                .QueryAsync(new(SessionId: sessionId, PageSize: 128));
            Assert.True(cycles.Available, cycles.ReasonCode);
            coreHash = Assert.Single(cycles.Events, value => value.Kind == QualificationCycleEventKind.CoreCommitted).ContentHash;
            controller.SetControllerCycle(62, 1);
            // GetSnapshotAsync reconciles identity and would wait on the
            // deliberately held lease. A pre-established stream observes the
            // published immutable health without reacquiring that lease.
            while (snapshots.Current.PlcCommunication?.ReasonCode != "QualificationControllerEpochChanged")
                Assert.True(await snapshots.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { release.Set(); }
        var history = await harness.WaitForHistoryAsync(sessionId, page => page.Events.Any(value =>
            value.ReasonCode == "QualificationModbusRecoveryRequired"), "communication core recovery retirement");
        Assert.Equal(ExecutionStatus.Success, Assert.Single(history.Runs).ExecutionStatus);
        var after = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(SessionId: sessionId, PageSize: 128));
        Assert.True(after.Available, after.ReasonCode);
        Assert.Equal(coreHash, Assert.Single(after.Events, value => value.Kind == QualificationCycleEventKind.CoreCommitted).ContentHash);
        Assert.DoesNotContain(after.Events, value => value.Kind == QualificationCycleEventKind.PublicationPrepared);
        Assert.Equal(0, controller.ResultValidHighCount);
    }
}
