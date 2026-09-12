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
        await using var observation = new StagedStopObservation(harness.Runtime);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Ready before staged Graceful Stop");
        if (stage == "Acquisition") harness.PauseClock();
        if (stage == "Execution") harness.Factory.HoldExecution();
        try
        {
            peer.RaiseTrigger(61, 1);
            // Ready is an observation, not a reservation of future admission. Keep the
            // single trigger and prove its durable acceptance before testing Stop phases.
            await WaitForProductionHistoryAsync(harness,
                page => page.Events.Any(value => value.Kind == ProductionInspectionEventKind.Admitted &&
                    value.Admission.ControllerCycle.ControllerEpoch == 61 &&
                    value.Admission.ControllerCycle.CycleSequence == 1),
                "Durably accepted cycle before staged Graceful Stop: " + stage);
            if (stage == "Execution")
                await harness.Factory.ExecutionEntered.WaitAsync(TimeSpan.FromSeconds(10));
            else if (stage == "Payload")
                await peer.WaitForPayloadWriteAsync().WaitAsync(TimeSpan.FromSeconds(10));
            else if (stage == "AckHigh")
                await WaitProductionAsync(harness, state => state.Handshake == HandshakePhase.AwaitingResultAck, "AwaitAckHigh");
            else if (stage == "AckLow")
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
        catch (Exception error) when (error is Xunit.Sdk.XunitException or TimeoutException)
        {
            string history;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var page = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
                    .QueryAsync(new(PageSize: 128), timeout.Token);
                history = $"available={page.Available}/{page.ReasonCode}:" + string.Join(";", page.Events.Select(value =>
                    $"{value.Kind}/{value.ReasonCode}/{value.Admission.ControllerCycle}"));
            }
            catch (Exception diagnosticError) when (diagnosticError is not OutOfMemoryException)
            {
                history = "HistoryDiagnosticFailed:" + diagnosticError.GetType().Name + ":" + diagnosticError.Message;
            }
            throw new Xunit.Sdk.XunitException(error +
                $"\nStage={stage}; database={harness.Fixture.Options.DatabasePath}; history={history}" +
                $"\nPeer validHigh={peer.ResultValidHighCount},validLow={peer.ResultValidLowCount}," +
                $"ackHigh={peer.AckHighCount},ackLow={peer.AckLowCount},readyWrites={peer.ProductionReadyWriteCount}," +
                $"busy={peer.RuntimeBusy},valid={peer.RuntimeResultValid},ack={peer.ControllerResultAck}; " +
                "writes=" + string.Join(";", peer.StateWrites.TakeLast(16)) + "\n" + observation.Read());
        }
        finally
        {
            harness.ResumeClock();
            harness.Factory.ReleaseExecution();
            peer.ReleasePayloadWrite();
        }
    }

    private sealed class StagedStopObservation : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _changes = new();
        private readonly Task _worker;

        internal StagedStopObservation(IStationRuntime runtime)
        {
            _worker = Task.Run(async () =>
            {
                string? previous = null;
                try
                {
                    await foreach (var state in runtime.WatchSnapshotsAsync(_stop.Token))
                    {
                        var value = $"arm={state.ArmState},ready={state.Ready},busy={state.Busy}," +
                            $"phase={state.Handshake},execution={state.CurrentExecution},recovery={state.Recovery}," +
                            $"canArm={state.ProductionAdmission?.CanArm},audit={state.AuditIntegrity?.State}/" +
                            $"{state.AuditIntegrity?.ReasonCode};gates=" + string.Join(";",
                                state.ProductionAdmission?.Gates.Where(gate => gate.Status is not
                                    (ProductionAdmissionGateStatus.Passed or ProductionAdmissionGateStatus.NotApplicable))
                                    .Select(gate => gate.Gate + ":" + gate.ReasonCode) ?? Array.Empty<string>());
                        if (value != previous)
                        {
                            _changes.Enqueue($"{state.ObservedAtUtc:O}/{state.Revision}: {value}");
                            while (_changes.Count > 128) _changes.TryDequeue(out _);
                            previous = value;
                        }
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            });
        }

        internal string Read() => string.Join("\n", _changes);
        public async ValueTask DisposeAsync() { _stop.Cancel(); await _worker; _stop.Dispose(); }
    }
}
