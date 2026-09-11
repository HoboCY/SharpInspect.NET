using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// T46 recovery-boundary coverage for the two reachable "stop/restart while a mapped PLC recipe
/// change exists" states. Both instances drive the real SQLite writer, the real Modbus loopback
/// controller, the real Recipe Activation service and the real algorithm preparation; no wire
/// response, acknowledgement, activation record or recovery success is fabricated here.
///
/// V146_E09: a physical-console Graceful Production Stop is submitted through the real
/// Runtime.Submit while the mapped candidate is inside real algorithm preparation and before the
/// candidate camera is touched. With no Inspection Run, software disarm and the cancelled
/// activation can finish before the external callback returns. The preparation service retains
/// that callback until its actual retirement. The test observes all three facts separately,
/// preserves the previous Active record and verifies that the cancelled candidate never touches
/// a camera. Fixed busy responses, if any, are acknowledged before a fresh request identity.
///
/// V146_E10: a real loopback response was published and never acknowledged. The missing
/// acknowledgement faults the handshake (durable ProtocolFault + RecoveryRequired) without
/// inventing ACK / ResponseCleared / ResetObserved. The controller then zeroes its own request
/// fields, the process and store are restarted and a cold runtime is composed against the same
/// database: the residual runtime-owned response is still on the wire, is never cleared,
/// rewritten or replayed, and the cold owner refuses to join the session (Recovery Required,
/// never Ready). ADR-0102 only permits automatic Controller Synchronization for an idle clean
/// session and the parent specification leaves automatic reconciliation undefined, so this
/// refusal is asserted as the required behaviour; no automatic clearer may be added for it.
///
/// 票据限制：本票目前没有独立的换方人工撤销入口 —— 既没有清理遗留 runtime-owned response 的
/// 操作者命令，也没有可借用的 InspectionId 走 T44 人工恢复；启动对账只终结未完成的换方回合，
/// 绝不清除物理应答寄存器、也不重放请求。因此遗留应答只能阻断干净重连（fail closed），测试与
/// 注释都不得把这种阻断描述成"恢复成功"。
/// </summary>
public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V146_E09_LocalStopCancelsPlcActivationWhilePreparationCallbackRetiresSeparately()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!;
        await EnableRecipeChangeAsync(harness, previous);
        await ArmProductionAsync(harness);
        await WaitConditionAsync(() => peer.PhysicalProductionReady, "Ready before the local stop");
        var query = new SqliteRecipeSelectionQuery(harness.Fixture.Options);
        var preparedBefore = harness.Factory.Created;
        var deviceBefore = harness.CameraProvider.GetDiagnostics().Devices.Single();

        harness.Factory.HoldNextCreate();
        uint sequence = 0;
        try
        {
            sequence = await RequestRecipeChangeWithFreshBusyAttemptsAsync(harness, peer, 109,
                harness.Factory.NextCreateEntered);
            await WaitForFactoryCreateEntryAsync(harness);

            // The mapped activation owns the candidate and is inside the real algorithm
            // factory. StageAsync reserves the candidate camera configuration only after
            // preparation returns, so the candidate camera is provably untouched here.
            Assert.False(peer.PhysicalProductionReady);
            Assert.Equal(preparedBefore, harness.Factory.Created);
            var deviceDuring = harness.CameraProvider.GetDiagnostics().Devices.Single();
            Assert.Equal(deviceBefore.OpenCount, deviceDuring.OpenCount);
            Assert.Equal(deviceBefore.ConfigurationCursor, deviceDuring.ConfigurationCursor);

            var stop = await harness.Runtime.SubmitAsync(new GracefulProductionStopCommand(
                Guid.NewGuid(), harness.Invocation()));
            // StopAdmitted records software disarm admission. Its own durable completion,
            // the cancelled activation, and actual callback retirement are separate observations.
            Assert.Equal(CommandDisposition.Accepted, stop.Disposition);
            Assert.Equal("StopAdmitted", stop.ReasonCode);
            Assert.Equal(AuditPersistence.Persisted, stop.Audit);
            var stopTrace = await new SqliteCommandTraceQuery(harness.Fixture.Options)
                .QueryAsync(new CommandTraceFilter(CorrelationId: stop.CorrelationId, PageSize: 20));
            Assert.Contains(stopTrace.Records, record =>
                record.Phase == CommandAuditPhase.Outcome && record.ReasonCode == "StopAdmitted");
            var held = await query.QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
            Assert.True(held.Available, held.ReasonCode);
            Assert.Contains(held.Events, value => value.Kind == RecipeChangeEventKind.RequestObserved);
            Assert.DoesNotContain(held.Events, value => value.Kind == RecipeChangeEventKind.ProtocolFault ||
                value.Outcome == RecipeChangeOutcome.Succeeded);
            // Cancellation can publish the failed activation while the abandoned external
            // callback is still owned by AlgorithmPreparationService. It cannot touch a camera.
            await WaitRecipeResponseAsync(harness, peer);
            var cancelled = await WaitForPlcActivationTerminalAsync(harness, sequence, "PLC cancellation terminal missing");
            Assert.Equal(RecipeActivationOutcomeState.Cancelled, cancelled.Outcome.State);
            Assert.Equal(1, harness.Service<AlgorithmPreparationService>().PendingPreparationCount);
            Assert.Equal(preparedBefore, harness.Factory.Created);
            Assert.Equal(deviceBefore.ConfigurationCursor,
                harness.CameraProvider.GetDiagnostics().Devices.Single().ConfigurationCursor);
            var stopDeadline = DateTime.UtcNow.AddSeconds(15);
            while (true)
            {
                var trace = await new SqliteCommandTraceQuery(harness.Fixture.Options)
                    .QueryAsync(new CommandTraceFilter(CorrelationId: stop.CorrelationId, PageSize: 20));
                if (trace.Records.Any(value => value.Phase == CommandAuditPhase.Completed &&
                    value.ReasonCode == "LocallyDisarmed")) break;
                Assert.True(DateTime.UtcNow < stopDeadline, "Local software disarm terminal missing");
                await Task.Delay(20);
            }
            Assert.Equal(previous.Reference, (await harness.Activations.ReadCurrentAsync()).Record!.Reference);
        }
        finally
        {
            // The external factory callback deliberately ignores the Runtime token; it is
            // released here even when an assertion fails so the real cancellation can retire
            // the owner before the harness disposes the runtime.
            harness.Factory.ReleaseNextCreate();
        }
        await WaitConditionAsync(() => harness.Service<AlgorithmPreparationService>().PendingPreparationCount == 0,
            "Abandoned candidate callback did not retire after release");

        var terminal = await WaitForPlcActivationTerminalAsync(harness, sequence,
            "Held PLC candidate preparation did not retire after the local stop");
        Assert.False(terminal.Outcome.Succeeded);
        Assert.Equal(RecipeActivationOutcomeState.Cancelled, terminal.Outcome.State);
        Assert.Equal("RecipeActivationCancelled", terminal.Outcome.ReasonCode);
        Assert.Equal(RecipeActivationRestorationState.NotRequired, terminal.Restoration.State);
        Assert.Equal(previous.Reference, terminal.PreviousActivation);
        Assert.Equal(previous.Reference, (await harness.Activations.ReadCurrentAsync()).Record!.Reference);

        // Only the real fixed failure response ends the PLC request, and it is published after
        // the activation terminal above; the cancelled candidate never reaches Success.
        await WaitRecipeResponseAsync(harness, peer);
        var response = peer.RecipeChangeResponse;
        Assert.Equal((ushort)RecipeChangeOutcome.FailedActivation, response.Outcome);
        Assert.Equal((ushort)RecipeChangeReason.ActivationFailed, response.Reason);
        Assert.Equal(sequence, response.RequestSequence);
        Assert.Equal(7u, response.SelectionCode);
        Assert.False(peer.PhysicalProductionReady);
        var decided = await query.QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
        Assert.True(decided.Available, decided.ReasonCode);
        Assert.Single(decided.Events, value => value.Request.RequestSequence == sequence &&
            value.Kind == RecipeChangeEventKind.DecisionCommitted &&
            value.Outcome == RecipeChangeOutcome.FailedActivation &&
            value.Reason == RecipeChangeReason.ActivationFailed &&
            value.ReasonCode == "RecipeActivationCancelled");

        // The local Stop's own terminal is a software disarm; it is observed separately from
        // the change's outcome. After both, the controller still owns the ACK and reset
        // transitions, which this test completes over the real loopback.
        await CompleteRecipeChangeAsync(harness, peer, query);
        var state = await harness.Runtime.GetSnapshotAsync();
        Assert.False(state.Ready);
        Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
        Assert.Equal(RecoveryState.None, state.Recovery);
        var deviceAfter = harness.CameraProvider.GetDiagnostics().Devices.Single();
        Assert.Equal(deviceBefore.OpenCount, deviceAfter.OpenCount);
        Assert.Equal(deviceBefore.ConfigurationCursor, deviceAfter.ConfigurationCursor);
        var page = await harness.Activations.QueryAsync(new(PageSize: 20));
        Assert.True(page.Available, page.ReasonCode);
        var plcRecords = page.Records.Where(value => value.Actor?.PlcRequestContext?.RequestSequence == sequence).ToArray();
        // Exactly one PLC operation: its durable admission retired by exactly one terminal
        // record. The Stop must not have produced a second activation or a restoration pass.
        Assert.Single(plcRecords, value => value.IsTerminal);
        Assert.All(plcRecords.Where(value => !value.IsTerminal), value =>
            Assert.Equal(RecipeActivationOutcomeState.Admitted, value.Outcome.State));
        Assert.Single(plcRecords.Select(value => value.OperationId).Distinct());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V146_E10_PublishedUnacknowledgedResponseSurvivesRestartWithoutAutomaticRecovery(
        bool onlyDedicatedResponseRetained)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(2)));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!;
        var productionOptions = harness.Service<ProductionInspectionOptions>();
        var query = new SqliteRecipeSelectionQuery(harness.Fixture.Options);
        var responseWrites = peer.Writes.Count(value => value.StartAddress == 600);

        // The default Local Operator Only policy rejects the request, but the rejection is still
        // a real runtime-owned publication on the dedicated response block.
        peer.RequestRecipeChange(110, 7);
        await WaitRecipeResponseAsync(harness, peer);
        var published = peer.RecipeChangeResponse;
        Assert.True(published.ResponseValid);
        Assert.Equal((ushort)RecipeChangeOutcome.RejectedUnknownCode, published.Outcome);
        Assert.Equal((ushort)RecipeChangeReason.LocalOperatorOnly, published.Reason);
        Assert.Equal(110u, published.RequestSequence);
        Assert.Equal(7u, published.SelectionCode);
        Assert.Equal(responseWrites + 1, peer.Writes.Count(value => value.StartAddress == 600));

        // The controller never acknowledges. The handshake must fault on its deadline without
        // inventing ACK / ResponseCleared / ResetObserved, and the published response must stay.
        var faulted = await WaitForRecipeChangeProtocolFaultAsync(harness, query,
            "Missing acknowledgement did not fault the dedicated handshake");
        var fault = Assert.Single(faulted.Events, value => value.Kind == RecipeChangeEventKind.ProtocolFault);
        // The fault keeps the frozen prior decision and names the timeout only in its own
        // reason code; a missing acknowledgement may never rewrite the decision.
        Assert.Equal(RecipeChangeOutcome.RejectedUnknownCode, fault.Outcome);
        Assert.Equal(RecipeChangeReason.LocalOperatorOnly, fault.Reason);
        Assert.Equal("RecipeChangeHandshakeDeadlineExceeded", fault.ReasonCode);
        Assert.Single(faulted.Events, value => value.Kind == RecipeChangeEventKind.DecisionCommitted &&
            value.Outcome == RecipeChangeOutcome.RejectedUnknownCode &&
            value.Reason == RecipeChangeReason.LocalOperatorOnly &&
            value.ReasonCode == "RecipeChangeLocalOperatorOnly");
        Assert.DoesNotContain(faulted.Events, value => value.Kind is RecipeChangeEventKind.AcknowledgementObserved
            or RecipeChangeEventKind.ResponseCleared or RecipeChangeEventKind.ResetObserved);
        var faultedState = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(RecoveryState.Required, faultedState.Recovery);
        Assert.Equal(published, peer.RecipeChangeResponse);
        Assert.Equal(responseWrites + 1, peer.Writes.Count(value => value.StartAddress == 600));
        var withoutActivation = await harness.Activations.QueryAsync(new(PageSize: 64));
        Assert.True(withoutActivation.Available, withoutActivation.ReasonCode);
        Assert.DoesNotContain(withoutActivation.Records, value => value.Actor?.IsPlcAdapter == true);
        Assert.Equal(previous.Reference, (await harness.Activations.ReadCurrentAsync()).Record!.Reference);

        // The controller clears only its own request fields. The runtime-owned response block is
        // not controller-writable and stays exactly as published across the owner retirement.
        peer.ResetRecipeChange();
        await harness.StopRuntimePreservingFixtureAsync();
        Assert.Equal(published, peer.RecipeChangeResponse);
        if (onlyDedicatedResponseRetained)
        {
            // Isolated initial image only; this is not a product or controller recovery action.
            peer.SeedRetainedRuntimeState(false, false, false, false);
            Assert.True(peer.IsRuntimeClear);
        }
        Assert.False(peer.RecipeChangeController.Request);
        Assert.False(peer.RecipeChangeController.Acknowledgement);
        Assert.Equal(0u, peer.RecipeChangeController.RequestSequence);
        Assert.Equal(0u, peer.RecipeChangeController.SelectionCode);
        Assert.Equal(published, peer.RecipeChangeResponse);
        var dedicatedReadsBefore = peer.RecipeChangeResponseReadCount;

        await harness.Fixture.RestartStoreAsync();
        var clock = new VirtualCameraClock(DateTimeOffset.UtcNow);
        await using var pump = ClockPump.Start(clock);
        var provider = ManualHarness.CreateProvider(clock, timeout: false);
        var connectionsBefore = peer.ConnectionCount;
        var requestsBefore = peer.RequestCount;
        var restarted = CreateRestartedManualRuntime(harness, new[] { provider }, clock);
        try
        {
            restarted.ConfigureProductionInspections(productionOptions, harness.Fixture.Options,
                harness.Service<AlgorithmExecutionOptions>(), clock);
            // A clean controller side plus a residual runtime-owned response is not a clean
            // session: the cold owner must keep Recovery Required and never become Ready. There
            // is no operator entry point in this ticket which could revoke the residue, so the
            // refusal is the expected outcome rather than a recovery.
            var cold = await WaitForColdProductionRefusalAsync(restarted, peer, connectionsBefore, requestsBefore,
                "Cold runtime joined a session with a residual runtime-owned recipe-change response");
            Assert.NotEqual(faultedState.RuntimeEpoch, cold.RuntimeEpoch);
            Assert.False(cold.Ready);
            Assert.Equal(ProductionArmState.Disarmed, cold.ArmState);
            Assert.Equal(RecoveryState.Required, cold.Recovery);
            if (onlyDedicatedResponseRetained)
            {
                Assert.True(peer.RecipeChangeResponseReadCount > dedicatedReadsBefore,
                    "Cold owner never sampled the retained dedicated response block");
                Assert.False(cold.PlcCommunication!.Synchronized);
            }

            var coldActivations = new SqliteRecipeActivationQuery(harness.Fixture.Options);
            var activationPage = await coldActivations.QueryAsync(new(PageSize: 64));
            Assert.True(activationPage.Available, activationPage.ReasonCode);
            Assert.DoesNotContain(activationPage.Records, value => value.Actor?.IsPlcAdapter == true);
            var current = await coldActivations.ReadCurrentAsync();
            Assert.True(current.Available, current.ReasonCode);
            Assert.Equal(previous.Reference, current.Record!.Reference);

            var coldHistory = await new SqliteRecipeSelectionQuery(harness.Fixture.Options)
                .QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
            Assert.True(coldHistory.Available, coldHistory.ReasonCode);
            Assert.Single(coldHistory.Events, value => value.Kind == RecipeChangeEventKind.RequestObserved);
            Assert.Single(coldHistory.Events, value => value.Kind == RecipeChangeEventKind.DecisionCommitted);
            Assert.Single(coldHistory.Events, value => value.Kind == RecipeChangeEventKind.ResponsePublished);
            Assert.DoesNotContain(coldHistory.Events, value => value.Kind is RecipeChangeEventKind.AcknowledgementObserved
                or RecipeChangeEventKind.ResponseCleared or RecipeChangeEventKind.ResetObserved);

            // No replay: the response block was written exactly once in the whole scenario and
            // the controller-owned request block was never written by the runtime at all.
            Assert.Equal(responseWrites + 1, peer.Writes.Count(value => value.StartAddress == 600));
            Assert.DoesNotContain(peer.Writes, value => value.StartAddress == 500);
            Assert.Equal(published, peer.RecipeChangeResponse);
        }
        finally
        {
            await restarted.DisposeAsync();
        }

        // Disposing the cold owner cannot silently clear the residual response either.
        Assert.Equal(published, peer.RecipeChangeResponse);
        Assert.Equal(responseWrites + 1, peer.Writes.Count(value => value.StartAddress == 600));
    }

    private static async Task WaitForFactoryCreateEntryAsync(ManualHarness harness)
    {
        try
        {
            await harness.Factory.NextCreateEntered.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            var state = await harness.Runtime.GetSnapshotAsync();
            var history = await new SqliteRecipeSelectionQuery(harness.Fixture.Options)
                .QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
            Assert.Fail("PLC candidate preparation did not enter the real algorithm factory: " +
                state.LastCommand?.ReasonCode + "/" + string.Join(",", state.AdmissionBlockers) + "; " +
                string.Join(";", history.Events.Select(value => value.Kind + ":" + value.ReasonCode)));
        }
    }

    private static async Task<RecipeActivationRecord> WaitForPlcActivationTerminalAsync(ManualHarness harness,
        uint sequence, string reason)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var page = await harness.Activations.QueryAsync(new(PageSize: 20));
            Assert.True(page.Available, page.ReasonCode);
            var terminal = page.Records.SingleOrDefault(value =>
                value.Actor?.PlcRequestContext?.RequestSequence == sequence && value.IsTerminal);
            if (terminal is not null) return terminal;
            var state = await harness.Runtime.GetSnapshotAsync();
            Assert.True(DateTime.UtcNow < deadline, reason + ": " + state.LastCommand?.ReasonCode + "/" +
                state.Recovery + "/" + string.Join(",", state.AdmissionBlockers));
            await Task.Delay(20);
        }
    }

    private static async Task<RecipeChangeHistoryPage> WaitForRecipeChangeProtocolFaultAsync(ManualHarness harness,
        SqliteRecipeSelectionQuery query, string reason)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var history = await query.QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
            Assert.True(history.Available, history.ReasonCode);
            if (history.Events.Any(value => value.Kind == RecipeChangeEventKind.ProtocolFault)) return history;
            var state = await harness.Runtime.GetSnapshotAsync();
            Assert.True(DateTime.UtcNow < deadline, reason + ": " + state.Recovery + "/" +
                state.LastCommand?.ReasonCode + "/" + state.PlcCommunication?.ReasonCode);
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Waits for the cold owner's fail-closed refusal. A healthy store proves the cold runtime
    /// finished its own startup, and the increased transport counters prove it really opened a
    /// session and sampled the loopback controller. The refusal itself is the durable Recovery
    /// Required with Ready false: the owner either refuses the residual runtime-owned response
    /// block or refuses its own residual runtime state block first. Both refuse to join the
    /// session, and neither clears another owner's registers.
    /// </summary>
    private static async Task<StationStateSnapshot> WaitForColdProductionRefusalAsync(StationRuntime runtime,
        ModbusQualificationTestServer peer, int connectionsBefore, int requestsBefore, string reason)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        StationStateSnapshot? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await runtime.GetSnapshotAsync();
            if (last.Store.State == HealthState.Healthy && last.Recovery == RecoveryState.Required &&
                !last.Ready && last.ArmState == ProductionArmState.Disarmed &&
                peer.ConnectionCount > connectionsBefore && peer.RequestCount > requestsBefore) return last;
            await Task.Delay(25);
        }
        throw new XunitException(reason + ": " + last?.Store.State + "/" + last?.Store.ReasonCode + "/" +
            last?.Recovery + "/" + last?.Ready + "/" + last?.ArmState + "/" +
            string.Join(",", last?.AdmissionBlockers.AsEnumerable() ?? Array.Empty<string>()) + "/" +
            last?.PlcCommunication?.ReasonCode + "/connections=" + (peer.ConnectionCount - connectionsBefore) +
            "/requests=" + (peer.RequestCount - requestsBefore));
    }
}
