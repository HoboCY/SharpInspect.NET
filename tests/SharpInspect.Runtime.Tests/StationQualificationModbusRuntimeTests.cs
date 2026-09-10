using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.Qualification;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    public enum ModbusAlgorithmOutcomeMode
    {
        Error,
        Timeout,
        SelfCancelled
    }

    [Theory]
    [InlineData(ModbusAlgorithmOutcomeMode.Error, ExecutionStatus.Error)]
    [InlineData(ModbusAlgorithmOutcomeMode.Timeout, ExecutionStatus.Timeout)]
    // AlgorithmExecutionService intentionally treats an algorithm-originated
    // OperationCanceledException as a non-success error: only the Runtime
    // caller token can produce its typed Cancelled outcome.  The case still
    // proves the payload is Unknown and the controller handshake completes.
    [InlineData(ModbusAlgorithmOutcomeMode.SelfCancelled, ExecutionStatus.Error)]
    public async Task V140_R07_AlgorithmNonSuccessProducesUnknownPayloadAndNormalAck(
        ModbusAlgorithmOutcomeMode mode, ExecutionStatus expectedStatus)
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1, profileFactory: snapshot => controller.CreateProfile(snapshot),
            modbusAlgorithmOutcome: mode);

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "Modbus algorithm non-success start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "Modbus algorithm non-success session did not become ready");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(47, (uint)mode + 1);

        await controller.WaitForPayloadWriteAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await controller.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await controller.WaitForAckHighAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await controller.WaitForAckLowAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var closed = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId && snapshot.Phase == StationQualificationSessionPhase.Closed,
            "Modbus algorithm non-success session did not close");

        Assert.Equal(StationQualificationRestorationState.Restored, closed.Restoration);
        Assert.Equal(1, controller.ResultValidHighCount);
        Assert.Equal(1, controller.ResultValidLowCount);
        Assert.Equal(1, controller.AckHighCount);
        Assert.Equal(1, controller.AckLowCount);
        Assert.Equal(0, controller.ProductionReadyWriteCount);

        var history = await harness.WaitForHistoryAsync(sessionId,
            page => page.Runs.Any(value => value.Terminal),
            "Modbus algorithm non-success run was not persisted");
        var run = Assert.Single(history.Runs, value => value.SessionId == sessionId);
        Assert.True(run.Terminal);
        Assert.Equal(expectedStatus, run.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, run.Decision);
        Assert.NotNull(run.QualificationPayload);
        Assert.Equal(expectedStatus, run.QualificationPayload!.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, run.QualificationPayload.Decision);
        Assert.Null(run.ResultPayloadJson);
        Assert.Null(run.ResultPayloadHash);
        Assert.True(run.ReasonCode.Contains("Algorithm", StringComparison.OrdinalIgnoreCase),
            run.ReasonCode);
    }

    [Fact]
    public async Task V140_R02_InitialAckHighNeverAdvertisesReadyAndBlocksRecovery()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.SetInitialControllerAck(true);
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1, allowDisposeFailure: true,
            profileFactory: snapshot => controller.CreateProfile(snapshot));

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "Modbus initial-ack-high start");
        var blocked = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.RecoveryBlocked,
            "initial controller acknowledgement did not block qualification");
        var sessionId = Assert.IsType<Guid>(blocked.SessionId);

        Assert.Equal(0, controller.ReadyWriteCount);
        Assert.DoesNotContain(controller.StateWrites, value => value.QualificationReady);
        Assert.False(controller.RuntimeResultValid);

        var history = await harness.WaitForHistoryAsync(sessionId,
            page => page.Events.Any(value =>
                value.Phase == StationQualificationSessionPhase.RecoveryBlocked),
            "initial-ack-high RecoveryBlocked fact was not persisted");
        Assert.Empty(history.Runs);
        Assert.Contains(history.Events, value =>
            value.Phase == StationQualificationSessionPhase.RecoveryBlocked);
    }

    [Fact]
    public async Task V140_R03_AckStaysLowAfterPublicationAndPreservesValidPayloadOnTimeout()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.AutoAcknowledge = false;
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1, allowDisposeFailure: true,
            profileFactory: snapshot => controller.CreateProfile(snapshot,
                acknowledgementTimeout: TimeSpan.FromMilliseconds(100)));

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "Modbus low-ack start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "Modbus low-ack session did not become ready");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(42, 1);

        await controller.WaitForPayloadWriteAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await controller.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var blocked = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase == StationQualificationSessionPhase.RecoveryBlocked,
            "low acknowledgement timeout did not block recovery");

        Assert.True(controller.RuntimeResultValid);
        Assert.False(controller.ControllerResultAck);
        Assert.Equal(1, controller.ResultValidHighCount);
        Assert.Equal(0, controller.ResultValidLowCount);
        Assert.Equal(0, controller.AckHighCount);
        Assert.Equal(0, controller.AckLowCount);
        await WaitForQualificationAlarmAsync(harness, QualificationCycleAlarmCodes.ResultAckTimeout);
        Assert.Contains((await harness.Runtime.GetSnapshotAsync()).AlarmState!.Instances, value =>
            value.Code == QualificationCycleAlarmCodes.ResultAckTimeout &&
            value.Lifecycle != AlarmLifecycle.Cleared);

        var history = await harness.WaitForHistoryAsync(sessionId,
            page => page.Runs.Any(value => value.Terminal) &&
                page.Events.Any(value => value.Phase == StationQualificationSessionPhase.RecoveryBlocked),
            "low acknowledgement terminal facts were not persisted");
        var run = Assert.Single(history.Runs, value => value.SessionId == sessionId);
        Assert.True(run.Terminal);
        Assert.Equal(ExecutionStatus.Success, run.ExecutionStatus);
        Assert.NotNull(run.QualificationPayload);

        var cycles = await WaitForCycleHistoryAsync(harness, page =>
            page.Events.Any(value => value.Kind == QualificationCycleEventKind.AckTimeout),
            "low acknowledgement timeout cycle fact was not persisted");
        Assert.Contains(cycles.Events, value => value.Kind == QualificationCycleEventKind.AckTimeout &&
            value.Terminal);
    }

    [Fact]
    public async Task V140_R04_AckHighWithoutLowResetPreservesPayloadAndBlocksRecovery()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.AutoClearAcknowledge = false;
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1, allowDisposeFailure: true,
            profileFactory: snapshot => controller.CreateProfile(snapshot,
                acknowledgementTimeout: TimeSpan.FromMilliseconds(100)));

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "Modbus low-reset start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "Modbus low-reset session did not become ready");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(42, 2);

        await controller.WaitForPayloadWriteAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await controller.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await controller.WaitForAckHighAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var blocked = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase == StationQualificationSessionPhase.RecoveryBlocked,
            "low-reset acknowledgement timeout did not block recovery");

        Assert.False(controller.RuntimeResultValid);
        Assert.True(controller.ControllerResultAck);
        Assert.Equal(1, controller.ResultValidHighCount);
        Assert.Equal(1, controller.ResultValidLowCount);
        Assert.Equal(1, controller.AckHighCount);
        Assert.Equal(0, controller.AckLowCount);
        await WaitForQualificationAlarmAsync(harness, QualificationCycleAlarmCodes.ResultAckTimeout);
        Assert.Contains((await harness.Runtime.GetSnapshotAsync()).AlarmState!.Instances, value =>
            value.Code == QualificationCycleAlarmCodes.ResultAckTimeout &&
            value.Lifecycle != AlarmLifecycle.Cleared);

        var history = await harness.WaitForHistoryAsync(sessionId,
            page => page.Runs.Any(value => value.Terminal) &&
                page.Events.Any(value => value.Phase == StationQualificationSessionPhase.RecoveryBlocked),
            "low-reset terminal facts were not persisted");
        var run = Assert.Single(history.Runs, value => value.SessionId == sessionId);
        Assert.True(run.Terminal);
        Assert.Equal(ExecutionStatus.Success, run.ExecutionStatus);
        Assert.NotNull(run.QualificationPayload);

        var cycles = await WaitForCycleHistoryAsync(harness, page =>
            page.Events.Any(value => value.Kind == QualificationCycleEventKind.AckTimeout),
            "low-reset acknowledgement timeout cycle fact was not persisted");
        Assert.Contains(cycles.Events, value => value.Kind == QualificationCycleEventKind.AckTimeout &&
            value.Terminal);
    }

    [Fact]
    public async Task V140_R05_BusySecondKeyAndHeldHighDuplicateRejectWithoutSecondRun()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.AutoAcknowledge = false;
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1, profileFactory: snapshot => controller.CreateProfile(snapshot,
                acknowledgementTimeout: TimeSpan.FromSeconds(15)));

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "Modbus busy-rejection start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "Modbus busy-rejection session did not become ready");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(43, 1);
        await controller.WaitForPayloadWriteAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await controller.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(controller.RuntimeBusy);

        var secondKey = controller.WaitForControllerSampleAsync(43, 2);
        controller.RaiseTrigger(43, 2);
        await secondKey.WaitAsync(TimeSpan.FromSeconds(10));
        var oneRejection = await WaitForCycleHistoryAsync(harness, page =>
            page.Events.Count(value => value.Kind == QualificationCycleEventKind.ProtocolRequestRejected) >= 1,
            "busy second key rejection was not persisted");
        Assert.Contains(oneRejection.Events, value =>
            value.Kind == QualificationCycleEventKind.ProtocolRequestRejected &&
            value.ReasonCode == "QualificationTriggerRejectedWhileNotReady");

        controller.ArmControllerLowObservation();
        controller.SetTrigger(false);
        await controller.WaitForControllerLowObservedAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var duplicateKey = controller.WaitForControllerSampleAsync(43, 2);
        controller.RaiseTrigger(43, 2);
        await duplicateKey.WaitAsync(TimeSpan.FromSeconds(10));
        var twoRejections = await WaitForCycleHistoryAsync(harness, page =>
            page.Events.Count(value => value.Kind == QualificationCycleEventKind.ProtocolRequestRejected) >= 2,
            "held-high duplicate rejection was not persisted");
        Assert.Contains(twoRejections.Events, value =>
            value.Kind == QualificationCycleEventKind.ProtocolRequestRejected &&
            value.ReasonCode == "QualificationDuplicateCycleRejected");

        controller.ArmControllerLowObservation();
        controller.SetTrigger(false);
        await controller.WaitForControllerLowObservedAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.SetControllerCycle(43, 1);
        controller.AcknowledgeResult();
        await controller.WaitForAckHighAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await controller.WaitForAckLowAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var closed = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId && snapshot.Phase == StationQualificationSessionPhase.Closed,
            "busy-rejection original run did not complete normally");
        Assert.Equal(StationQualificationRestorationState.Restored, closed.Restoration);

        var history = await harness.WaitForHistoryAsync(sessionId,
            page => page.Runs.Any(value => value.Terminal),
            "busy-rejection run terminal fact was not persisted");
        var run = Assert.Single(history.Runs, value => value.SessionId == sessionId);
        Assert.True(run.Terminal);
        Assert.Equal(ExecutionStatus.Success, run.ExecutionStatus);
        Assert.NotNull(run.QualificationPayload);
        var profile = Assert.IsType<ModbusQualificationProfile>(harness.ModbusProfile);
        var payload = Assert.IsType<StationQualificationPayload>(run.QualificationPayload);
        var expectedPayloadWrites = payload.Segments.Sum(segment =>
            (segment.RegisterCount + 122) / 123);
        Assert.Equal(expectedPayloadWrites, controller.Writes.Count(value => value.Function == 0x10 &&
            value.StartAddress != profile.RuntimeStartAddress));
        Assert.Equal(1, controller.ResultValidHighCount);
        Assert.Equal(1, controller.ResultValidLowCount);
        Assert.Equal(1, controller.AckHighCount);
        Assert.Equal(1, controller.AckLowCount);
        Assert.Contains((await harness.Runtime.GetSnapshotAsync()).AlarmState!.Instances, value =>
            value.Code == QualificationCycleAlarmCodes.TriggerRejected &&
            value.Lifecycle != AlarmLifecycle.Cleared);

        var finalCycles = await WaitForCycleHistoryAsync(harness, page =>
            page.Events.Any(value => value.Kind == QualificationCycleEventKind.AckReset),
            "busy-rejection cycle completion was not persisted");
        Assert.Equal(1, finalCycles.Events.Count(value => value.Kind == QualificationCycleEventKind.Admitted));
        Assert.Equal(2, finalCycles.Events.Count(value =>
            value.Kind == QualificationCycleEventKind.ProtocolRequestRejected));
        Assert.Contains(finalCycles.Events, value => value.Kind == QualificationCycleEventKind.AckReset);
    }

    [Fact]
    public async Task V140_R06_PayloadResponseLossPreservesCoreWithoutReplay()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.HoldFirstPayloadWrite = false;
        controller.DropNextPayloadResponse = true;
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1, allowDisposeFailure: true,
            profileFactory: snapshot => controller.CreateProfile(snapshot));

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "Modbus payload-response-loss start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "Modbus payload-response-loss session did not become ready");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(44, 1);
        await controller.WaitForPayloadWriteAsync().WaitAsync(TimeSpan.FromSeconds(20));

        var blocked = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase == StationQualificationSessionPhase.RecoveryBlocked,
            "payload response loss did not block recovery");
        var alarmDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < alarmDeadline &&
            !(await harness.Runtime.GetSnapshotAsync()).AlarmState!.Instances.Any(value =>
                value.Code == QualificationCycleAlarmCodes.Interrupted &&
                value.Lifecycle != AlarmLifecycle.Cleared))
            await Task.Delay(25);
        Assert.Contains((await harness.Runtime.GetSnapshotAsync()).AlarmState!.Instances, value =>
            value.Code == QualificationCycleAlarmCodes.Interrupted &&
            value.Lifecycle != AlarmLifecycle.Cleared);

        var history = await harness.WaitForHistoryAsync(sessionId,
            page => page.Runs.Any(value => value.Terminal) &&
                page.Events.Any(value => value.Phase == StationQualificationSessionPhase.RecoveryBlocked),
            "payload response loss did not preserve terminal Core facts");
        var run = Assert.Single(history.Runs, value => value.SessionId == sessionId);
        Assert.True(run.Terminal);
        Assert.Equal(ExecutionStatus.Success, run.ExecutionStatus);
        var payload = Assert.IsType<StationQualificationPayload>(run.QualificationPayload);
        Assert.NotEmpty(payload.Segments);

        var cycles = await WaitForCycleHistoryAsync(harness, page =>
            page.Events.Any(value => value.Kind == QualificationCycleEventKind.CoreCommitted),
            "payload response loss did not preserve CoreCommitted");
        Assert.Contains(cycles.Events, value => value.Kind == QualificationCycleEventKind.CoreCommitted);
        Assert.Contains(cycles.Events, value => value.Kind == QualificationCycleEventKind.PublicationPrepared);
        Assert.DoesNotContain(cycles.Events, value =>
            value.Kind == QualificationCycleEventKind.ResultValidPublished);

        var profile = Assert.IsType<ModbusQualificationProfile>(harness.ModbusProfile);
        var payloadWrites = controller.Writes.Where(value => value.Function == 0x10 &&
            value.StartAddress != profile.RuntimeStartAddress).ToArray();
        Assert.Single(payloadWrites);
        Assert.Equal(1, controller.ConnectionCount);
        Assert.Equal(0, controller.ResultValidHighCount);
        Assert.Equal(0, controller.ResultValidLowCount);
        Assert.False(controller.ControllerResultAck);
    }

    [Fact]
    public async Task V140_R01_ModbusCyclePersistsBeforePayloadAndCompletesControllerHandshake()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1,
            profileFactory: snapshot => controller.CreateProfile(snapshot));

        var profile = Assert.IsType<SharpInspect.Runtime.Qualification.ModbusQualificationProfile>(
            harness.ModbusProfile);
        Assert.Equal(profile.ContentHash, harness.Plan.ProfileHash);
        Assert.Equal(profile.EndpointBindingHash,
            harness.Plan.TransientControllerConfiguration.EndpointBindingHash);

        var accepted = await harness.StartWithFreshStepUpAsync();
        AssertAccepted(accepted, "Modbus qualification start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "Modbus qualification did not advertise readiness");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);

        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(controllerEpoch: 41, cycleSequence: 7);

        await controller.WaitForPayloadWriteAsync().WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            // The first payload request is deliberately held by the server. The
            // run and schema-26 CoreCommitted fact must already be durable before
            // Runtime can answer that request.
            var history = await harness.History.QueryAsync(new(
                SessionId: sessionId, PageSize: 128));
            Assert.True(history.Available, history.ReasonCode);
            var run = Assert.Single(history.Runs, value => value.SessionId == sessionId);
            Assert.True(run.Terminal, run.ReasonCode);
            Assert.NotNull(run.QualificationPayload);
            Assert.Equal(ExecutionStatus.Success, run.ExecutionStatus);

            var cycles = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
                .QueryAsync(new(SessionId: sessionId, PageSize: 128));
            Assert.True(cycles.Available, cycles.ReasonCode);
            Assert.Contains(cycles.Events, value => value.Kind == QualificationCycleEventKind.CoreCommitted);
        }
        finally
        {
            controller.ReleasePayloadWrite();
        }

        await controller.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await controller.WaitForAckLowAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var closed = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId && snapshot.Phase == StationQualificationSessionPhase.Closed,
            "Modbus qualification did not close after acknowledgement");

        Assert.Equal(StationQualificationRestorationState.Restored, closed.Restoration);
        Assert.False(closed.Ready);
        Assert.False(closed.ProductionAuthority);
        Assert.False(closed.CanIssueQualification);
        Assert.Equal(0, harness.Facility.WaitCount);
        Assert.Equal(0, harness.Facility.WriteCount);
        Assert.Equal(0, controller.ProductionReadyWriteCount);
        Assert.True(controller.ResultValidHighCount >= 1);
        Assert.True(controller.ResultValidLowCount >= 1);
        Assert.True(controller.AckHighCount >= 1);
        Assert.True(controller.AckLowCount >= 1);
        Assert.True(controller.IsRuntimeClear);

        var finalHistory = await harness.History.QueryAsync(new(
            SessionId: sessionId, PageSize: 128));
        var finalRun = Assert.Single(finalHistory.Runs, value => value.SessionId == sessionId);
        var payload = Assert.IsType<StationQualificationPayload>(finalRun.QualificationPayload);
        Assert.Equal(controller.RaiseTriggerEpoch, payload.ControllerEpoch);
        Assert.Equal(controller.RaiseTriggerSequence, payload.CycleSequence);
        Assert.Equal(harness.Plan.QualificationContextHash, payload.ContextHash);
        AssertPayloadSegmentsWereWritten(controller, profile, payload);

        var cycleHistory = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(SessionId: sessionId, PageSize: 128));
        Assert.True(cycleHistory.Available, cycleHistory.ReasonCode);
        Assert.Contains(cycleHistory.Events, value =>
            value.Kind == QualificationCycleEventKind.CoreCommitted);
        Assert.Contains(cycleHistory.Events, value =>
            value.Kind == QualificationCycleEventKind.ResultValidPublished);
        Assert.Contains(cycleHistory.Events, value =>
            value.Kind == QualificationCycleEventKind.ResultAckObserved);
        Assert.Contains(cycleHistory.Events, value =>
            value.Kind == QualificationCycleEventKind.ResultValidCleared);
        Assert.Contains(cycleHistory.Events, value => value.Kind == QualificationCycleEventKind.AckReset);
    }

    private static async Task WaitForQualificationAlarmAsync(QualificationHarness harness, string code)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if ((await harness.Runtime.GetSnapshotAsync()).AlarmState!.Instances.Any(value =>
                value.Code == code && value.Lifecycle != AlarmLifecycle.Cleared)) return;
            await Task.Delay(25);
        }
        throw new XunitException("Qualification alarm was not persisted: " + code);
    }

    private static void AssertPayloadSegmentsWereWritten(ModbusQualificationTestServer controller,
        SharpInspect.Runtime.Qualification.ModbusQualificationProfile profile,
        StationQualificationPayload payload)
    {
        var writes = controller.Writes
            .Where(value => value.Function == 0x10 &&
                value.StartAddress != profile.RuntimeStartAddress)
            .ToArray();
        Assert.NotEmpty(writes);
        foreach (var expected in payload.Segments)
        {
            var actual = new byte[expected.RegisterBytes.Count];
            var expectedStart = expected.StartRegister;
            var expectedEnd = expectedStart + expected.RegisterBytes.Count / 2;
            foreach (var write in writes)
            {
                var writeStart = write.StartAddress;
                var writeEnd = writeStart + write.RegisterBytes.Length / 2;
                var overlapStart = Math.Max(expectedStart, writeStart);
                var overlapEnd = Math.Min(expectedEnd, writeEnd);
                if (overlapStart >= overlapEnd) continue;
                var sourceOffset = (overlapStart - writeStart) * 2;
                var targetOffset = (overlapStart - expectedStart) * 2;
                var bytes = (overlapEnd - overlapStart) * 2;
                Buffer.BlockCopy(write.RegisterBytes, sourceOffset, actual, targetOffset, bytes);
            }
            Assert.Equal(expected.RegisterBytes.ToArray(), actual);
        }
    }

    private static async Task<QualificationCycleHistoryPage> WaitForCycleHistoryAsync(
        QualificationHarness harness, Func<QualificationCycleHistoryPage, bool> predicate,
        string timeoutReason)
    {
        var query = new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options);
        QualificationCycleHistoryPage? last = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            last = await query.QueryAsync(new(PageSize: 128));
            Assert.True(last.Available, last.ReasonCode);
            if (predicate(last)) return last;
            await Task.Delay(25);
        }
        throw new XunitException(timeoutReason + ":" + last?.ReasonCode);
    }
}
