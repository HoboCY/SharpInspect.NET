using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public Task V147_R17_ReservedPlcRequestWinsTheOnceOnlyStartupCauseAcrossColdRestart() =>
        VerifyProductionArmCauseArbitrationAsync(plcFirst: true);

    [Fact]
    public Task V147_R18_PendingStartupCauseRefusesTheDedicatedPlcRequestWithoutActivation() =>
        VerifyProductionArmCauseArbitrationAsync(plcFirst: false);

    private static async Task VerifyProductionArmCauseArbitrationAsync(bool plcFirst)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)),
            productionArming: new ProductionArmStoreOptions(), productionArmStatusBinding: V147ArmStatusBinding,
            startupProductionPolicy: V147AutomaticStartup(), postActivationArmPolicy: V147AutomaticRearm(),
            productionArmMaintenance: new TestProductionArmMaintenanceProvider());
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!;
        await EnableRecipeChangeAsync(harness, previous);
        var productionOptions = harness.Service<ProductionInspectionOptions>();
        var executionOptions = harness.Service<AlgorithmExecutionOptions>();
        await harness.StopRuntimePreservingFixtureAsync();
        await harness.Fixture.RestartStoreAsync(restartIdentity: true);

        var clock = new VirtualCameraClock(DateTimeOffset.UtcNow);
        await using var pump = ClockPump.Start(clock);
        var provider = ManualHarness.CreateProvider(clock, timeout: false);
        var capture = new V147ArbitrationCaptureBarrier();
        await using var restarted = CreateRestartedManualRuntime(harness, new[] { provider }, clock, capture,
            productionDeploymentEvidence: true);
        await using var observation = new ProductionStateObservation(restarted);
        using var exceptions = new ProductionExceptionObservation();
        var maintenance = new TestProductionArmMaintenanceProvider().Attach(
            harness.Fixture.Options.LocalIdentity!.StationId, harness.Deployment!.ContentHash,
            new string('A', 64), ProductionArmMaintenanceState.ManualArmConfirmed);
        restarted.ConfigureProductionInspectionQualificationEvidenceProvider(issuer);
        restarted.ConfigureProductionArmMaintenanceEvidenceProvider(maintenance);
        const uint sequence = 321;
        const uint selectionCode = 7;
        if (plcFirst) peer.RequestRecipeChangeAfterHandshakeInitialization(sequence, selectionCode);
        try
        {
            restarted.ConfigureProductionInspections(productionOptions, harness.Fixture.Options,
                executionOptions, clock);
            // This is the actual main-loop refresh after once-only cause consideration.
            // The observer remains free to finish its real dedicated episode. The source
            // only blocks/denies; it never supplies a passing Runtime gate or CanArm.
            await capture.Entered.WaitAsync(TimeSpan.FromSeconds(20));
            if (plcFirst) Assert.True(peer.InitializationRecipeRequestInjected);
            else peer.RequestRecipeChange(sequence, selectionCode);
            try { await WaitForV147DedicatedResponseAsync(peer, sequence); }
            catch (XunitException exception)
            {
                var state = await restarted.GetSnapshotAsync();
                var failureHistory = await new SqliteRecipeSelectionQuery(harness.Fixture.Options)
                    .QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
                throw new XunitException(exception.Message + " state=" + state.Recovery + "/" + state.LastCommand +
                    ";communication=" + state.PlcCommunication + ";events=" + failureHistory.ReasonCode + ":" +
                    string.Join(";", failureHistory.Events.Select(value => value.Kind + ":" + value.ReasonCode)) +
                    ";observations=" + observation.Read() + ";exceptions=" + exceptions.Read());
            }
            var response = peer.RecipeChangeResponse;
            var plcSucceeded = plcFirst && response.Outcome == (ushort)RecipeChangeOutcome.Succeeded;
            var plcActivationBusy = plcFirst && response.Outcome == (ushort)RecipeChangeOutcome.FailedActivation;
            if (plcFirst ? !plcSucceeded && !plcActivationBusy :
                    response.Outcome != (ushort)RecipeChangeOutcome.RejectedBusy)
            {
                var diagnostic = await new SqliteRecipeSelectionQuery(harness.Fixture.Options)
                    .QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
                throw new XunitException("Unexpected arbitration response: " + response + ";history=" +
                    string.Join(";", diagnostic.Events.Select(value => value.Kind + ":" + value.ReasonCode)) +
                    ";state=" + (await restarted.GetSnapshotAsync()).LastCommand);
            }
            Assert.Equal(sequence, response.RequestSequence);
            Assert.Equal(selectionCode, response.SelectionCode);
            Assert.Equal((ushort)(plcSucceeded ? RecipeChangeReason.None :
                plcActivationBusy ? RecipeChangeReason.ActivationFailed : RecipeChangeReason.RuntimeBusy), response.Reason);
            var epoch = (await restarted.GetSnapshotAsync()).RuntimeEpoch;
            await CompleteV147DedicatedEpisodeAsync(harness, peer, sequence);
            var history = await new SqliteRecipeSelectionQuery(harness.Fixture.Options)
                .QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
            Assert.True(history.Available, history.ReasonCode);
            Assert.Equal(new[] { RecipeChangeEventKind.RequestObserved, RecipeChangeEventKind.DecisionCommitted,
                    RecipeChangeEventKind.ResponsePublished, RecipeChangeEventKind.AcknowledgementObserved,
                    RecipeChangeEventKind.ResponseCleared, RecipeChangeEventKind.ResetObserved },
                history.Events.Where(value => value.Request.RuntimeEpoch == epoch &&
                    value.Request.RequestSequence == sequence).Select(value => value.Kind));
            Assert.DoesNotContain(history.Events, value => value.Request.RuntimeEpoch == epoch &&
                value.Kind == RecipeChangeEventKind.ProtocolFault);
            if (plcActivationBusy)
                Assert.Equal("RecipeActivationRuntimeBusy", Assert.Single(history.Events, value =>
                    value.Request.RuntimeEpoch == epoch && value.Kind == RecipeChangeEventKind.DecisionCommitted).ReasonCode);
            else if (!plcFirst)
                Assert.Equal("ProductionArmAttemptInProgress", Assert.Single(history.Events, value =>
                    value.Request.RuntimeEpoch == epoch && value.Kind == RecipeChangeEventKind.DecisionCommitted).ReasonCode);
            capture.Release();
            // The next main-loop refresh follows ProcessAutomaticProductionArmAsync.
            // This proves the once-only cause has been processed, without a timer-only
            // absence assertion. A reserved activation may legitimately fail a later
            // nonblocking Runtime lock probe; it still must not leave a Startup cause.
            await capture.SecondEntered.WaitAsync(TimeSpan.FromSeconds(20));
            var observedArms = await new SqliteProductionArmHistoryQuery(harness.Fixture.Options)
                .QueryAsync(new ProductionArmHistoryFilter(PageSize: 128));
            Assert.True(observedArms.Available, observedArms.ReasonCode);
            if (plcFirst)
                Assert.DoesNotContain(observedArms.Events, value => value.RuntimeEpoch == epoch &&
                    value.Cause == ProductionArmCause.Startup);
            if (plcActivationBusy)
            {
                Assert.DoesNotContain(observedArms.Events, value => value.RuntimeEpoch == epoch);
                Assert.Equal(previous.Reference, (await harness.Activations.ReadCurrentAsync()).Record!.Reference);
                var unchanged = await restarted.GetSnapshotAsync();
                Assert.False(unchanged.Ready);
                Assert.False(peer.PhysicalProductionReady);
                Assert.Equal(RecoveryState.None, unchanged.Recovery);
                return;
            }

            var arms = await WaitForArmAttemptHistoryAsync(harness, page => page.Events.Any(value =>
                    value.RuntimeEpoch == epoch && value.Terminal), "Arbitrated automatic cause did not close");
            var attempted = Assert.Single(arms.Events, value => value.RuntimeEpoch == epoch &&
                value.Kind == ProductionArmEventKind.Attempted);
            Assert.Equal(plcFirst ? ProductionArmCause.PlcActivation : ProductionArmCause.Startup, attempted.Cause);
            Assert.DoesNotContain(arms.Events, value => value.RuntimeEpoch == epoch && value.Cause != attempted.Cause);
            if (plcFirst)
            {
                Assert.Equal(sequence, attempted.PlcRequest!.RequestSequence);
                Assert.Equal(selectionCode, attempted.PlcRequest.SelectionCode);
                var activated = (await harness.Activations.ReadCurrentAsync()).Record!;
                Assert.True(activated.Actor!.IsPlcAdapter);
                Assert.True(activated.Outcome.Succeeded);
            }
            else
                Assert.Equal(previous.Reference, (await harness.Activations.ReadCurrentAsync()).Record!.Reference);
            Assert.Equal(RecoveryState.None, (await restarted.GetSnapshotAsync()).Recovery);
            // Positive post-PLC Ready is tested in R04. This case proves cause/handshake
            // arbitration and keeps the cold process's ordinary qualification gates intact.
        }
        finally { capture.Release(); }
    }

    private sealed class V147ArbitrationCaptureBarrier : IProductionAdmissionFactsSource
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _secondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _captureCount;
        internal Task Entered => _entered.Task;
        internal Task SecondEntered => _secondEntered.Task;
        internal void Release() => _release.TrySetResult(true);
        public async ValueTask<ProductionAdmissionFacts> CaptureAsync(CancellationToken token)
        {
            _entered.TrySetResult(true);
            if (Interlocked.Increment(ref _captureCount) >= 2) _secondEntered.TrySetResult(true);
            await _release.Task.WaitAsync(token);
            throw new InvalidOperationException("V147SchedulingSourceUnavailable");
        }
    }

    private static async Task WaitForV147DedicatedResponseAsync(ModbusQualificationTestServer peer, uint sequence)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!peer.RecipeChangeResponse.ResponseValid || peer.RecipeChangeResponse.RequestSequence != sequence)
        {
            if (DateTime.UtcNow >= deadline)
                throw new XunitException("Dedicated response missing: " + peer.RecipeChangeResponse);
            await Task.Delay(20);
        }
    }

    private static async Task CompleteV147DedicatedEpisodeAsync(ManualHarness harness,
        ModbusQualificationTestServer peer, uint sequence)
    {
        peer.AcknowledgeRecipeChange();
        await WaitConditionAsync(() => !peer.RecipeChangeResponse.ResponseValid, "Dedicated response did not clear");
        peer.ResetRecipeChange();
        var query = new SqliteRecipeSelectionQuery(harness.Fixture.Options);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var history = await query.QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
            Assert.True(history.Available, history.ReasonCode);
            if (history.Events.Any(value => value.Request.RequestSequence == sequence &&
                value.Kind == RecipeChangeEventKind.ResetObserved)) return;
            if (DateTime.UtcNow >= deadline) throw new XunitException("Dedicated reset was not recorded");
            await Task.Delay(20);
        }
    }
}
