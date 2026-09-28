using System.Diagnostics;
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
/// T47 follow-up coverage for the F-A arbitration between the once-only start-up arm cause and
/// the dedicated PLC activation cause. Every case composes the real StationRuntime, the real
/// schema-32 arm ledger, the real qualification issuer and the controller-owned Modbus peer.
/// The development maintenance provider is the same explicit stand-in the other T47 Runtime
/// cases use: it never qualifies a deployment and never fabricates CanArm.
///
/// V147_R17 drives the ordering that the source arbitration protects: a request that already
/// owns the Runtime reservation when the once-only start-up cause is considered must win the
/// whole start-up decision. Its successful episode must close with the complete dedicated event
/// chain, no ProtocolFault and no recovery, exactly one automatic cause spent (the PLC
/// activation cause) and no start-up-caused attempt at all. The whole deployment is prepared in
/// a first Runtime and the case then runs against a real cold restart, so the request can still
/// be admitted by the very first sample. The request is asserted on the one boundary this
/// fixture can force without touching Runtime state: the dedicated handshake initialization is
/// the only response-block read that follows the previous one within two Modbus requests, while
/// every synchronized sample spends at least five requests between two response-block reads. A
/// dedicated spinning thread performs that assertion; when the operating system deschedules it
/// the request lands one sample later, which is observable as the same fixed RuntimeBusy refusal
/// a late request always receives. That attempt is discarded and the cold Runtime is rebuilt
/// instead of relaxing any assertion.
///
/// The in-lock re-check of a pending automatic attempt inside the reservation acquisition cannot
/// be reached by the current call order at all (the observer takes the reservation while the
/// first sample is still in flight, before the main loop can consider the once-only cause), so it
/// remains a defensive re-check without a black-box test.
///
/// V147_R18 covers the opposite direction deterministically: while the automatic start-up
/// attempt owns the Runtime the same dedicated request is refused with the fixed
/// RejectedBusy/ProductionArmAttemptInProgress decision, activates nothing and the start-up
/// attempt closes as the ordinary RuntimeBusy rejection.
/// </summary>
public sealed partial class ManualInspectionRuntimeTests
{
    private const uint V147ArbitrationSelectionCode = 7;
    private static readonly TimeSpan V147ArbitrationResponseTimeout = TimeSpan.FromSeconds(40);

    /// <summary>
    /// A five hundred millisecond synchronization window keeps every automatic attempt pending
    /// long enough for the case to observe it, while the twenty millisecond sample interval keeps
    /// the request-count signature of the initialization readable.
    /// </summary>
    private static PlcCommunicationPolicy V147ArbitrationCommunicationPolicy() => new(
        "V147.Arbitration.Communication", "1", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(40), 2, TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(30));

    [Fact]
    [Trait("VerificationId", "V147_R17")]
    public async Task V147_R17_ReservedPlcRequestWinsTheOnceOnlyStartUpCauseAcrossColdRestart()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)),
            productionArming: new ProductionArmStoreOptions(),
            productionArmStatusBinding: V147ArmStatusBinding,
            startupProductionPolicy: V147AutomaticStartup(),
            postActivationArmPolicy: V147AutomaticRearm(),
            productionArmMaintenance: new TestProductionArmMaintenanceProvider(),
            productionCommunicationPolicy: V147ArbitrationCommunicationPolicy());
        using var issuer = new ProductionTestIssuer();
        // The first process persists the whole deployment: released recipe, PLC result contract,
        // successful activation and the exact selection map entry the dedicated request carries.
        await PrepareProductionAsync(harness, issuer);
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!;
        await EnableRecipeChangeAsync(harness, previous);
        var productionOptions = harness.Service<ProductionInspectionOptions>();
        var executionOptions = harness.Service<AlgorithmExecutionOptions>();
        await harness.StopRuntimePreservingFixtureAsync();
        await harness.Fixture.RestartStoreAsync();

        var lostBoundary = new List<string>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var sequence = checked(311u + (uint)attempt * 10);
            var clock = new VirtualCameraClock(DateTimeOffset.UtcNow);
            var clockPump = ClockPump.Start(clock);
            var provider = ManualHarness.CreateProvider(clock, timeout: false);
            var restarted = CreateRestartedManualRuntime(harness, new[] { provider }, clock);
            var maintenance = new TestProductionArmMaintenanceProvider().Attach(
                harness.Fixture.Options.LocalIdentity!.StationId, harness.Deployment!.ContentHash,
                new string('A', 64), ProductionArmMaintenanceState.ManualArmConfirmed);
            restarted.ConfigureProductionInspectionQualificationEvidenceProvider(issuer);
            restarted.ConfigureProductionArmMaintenanceEvidenceProvider(maintenance);
            var gate = new V147AssertionGate();
            var assertion = StartRecipeChangeAtHandshakeInitialization(peer, sequence, V147ArbitrationSelectionCode, gate);
            try
            {
                restarted.ConfigureProductionInspections(productionOptions, harness.Fixture.Options,
                    executionOptions, clock);
                await WaitForDedicatedResponseAsync(peer, sequence, V147ArbitrationResponseTimeout);
                var response = peer.RecipeChangeResponse;
                if (!gate.BoundaryForced || response.Outcome != (ushort)RecipeChangeOutcome.Succeeded)
                {
                    // The barrier either lost its window (a plainly late request, which the Runtime
                    // refuses or admits without the once-only start-up cause ever being at stake) or
                    // the request was refused. Neither outcome can be the F-A collision this case
                    // protects: that collision leaves the accepted request and spends the start-up
                    // cause, so it is only visible in the ledger of a forced boundary. Discard the
                    // whole cold Runtime and force the boundary again; no assertion about the
                    // arbitration is relaxed to make this pass.
                    lostBoundary.Add(attempt + ": forced=" + gate.BoundaryForced + " outcome=" +
                        (RecipeChangeOutcome)response.Outcome + "/" + (RecipeChangeReason)response.Reason);
                    await CompleteDedicatedEpisodeAsync(harness, peer, sequence);
                    continue;
                }
                // The request was asserted at the initialization boundary, so the first sample of
                // the cold owner read it before the once-only start-up cause was considered.
                Assert.Equal((ushort)RecipeChangeOutcome.Succeeded, response.Outcome);
                Assert.Equal((ushort)RecipeChangeReason.None, response.Reason);
                Assert.Equal(sequence, response.RequestSequence);
                Assert.Equal(V147ArbitrationSelectionCode, response.SelectionCode);
                var coldEpoch = (await restarted.GetSnapshotAsync()).RuntimeEpoch;
                await CompleteDedicatedEpisodeAsync(harness, peer, sequence);

                var query = new SqliteRecipeSelectionQuery(harness.Fixture.Options);
                var history = await query.QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
                Assert.True(history.Available, history.ReasonCode);
                // The dedicated episode closes completely: no ProtocolFault and no recovery.
                Assert.Equal(new[] { RecipeChangeEventKind.RequestObserved, RecipeChangeEventKind.DecisionCommitted,
                        RecipeChangeEventKind.ResponsePublished, RecipeChangeEventKind.AcknowledgementObserved,
                        RecipeChangeEventKind.ResponseCleared, RecipeChangeEventKind.ResetObserved },
                    history.Events.Where(value => value.Request.RequestSequence == sequence).Select(value => value.Kind));
                Assert.DoesNotContain(history.Events, value => value.Request.RuntimeEpoch == coldEpoch &&
                    value.Kind == RecipeChangeEventKind.ProtocolFault);
                var activated = Assert.IsType<RecipeActivationRecord>(
                    (await harness.Activations.ReadCurrentAsync()).Record);
                Assert.True(activated.Actor!.IsPlcAdapter);
                Assert.True(activated.Outcome.Succeeded);

                // Exactly one automatic cause is spent in the cold epoch and it is the PLC
                // activation cause; the once-only start-up cause was never spent at all.
                var armHistory = await WaitForArmAttemptHistoryAsync(harness, page => page.Events.Any(entry =>
                        entry.RuntimeEpoch == coldEpoch && entry.Cause == ProductionArmCause.PlcActivation &&
                        entry.Terminal),
                    "The PLC activation attempt was not recorded and closed");
                var attempted = Assert.Single(armHistory.Events, entry => entry.RuntimeEpoch == coldEpoch &&
                    entry.Kind == ProductionArmEventKind.Attempted);
                Assert.Equal(ProductionArmCause.PlcActivation, attempted.Cause);
                Assert.Equal(sequence, attempted.PlcRequest!.RequestSequence);
                Assert.Equal(V147ArbitrationSelectionCode, attempted.PlcRequest.SelectionCode);
                Assert.DoesNotContain(armHistory.Events, entry => entry.RuntimeEpoch == coldEpoch &&
                    entry.Cause == ProductionArmCause.Startup);
                var terminal = Assert.Single(armHistory.Events, entry => entry.Terminal &&
                    entry.AttemptId == attempted.AttemptId);
                Assert.NotEqual(ProductionArmReason.None, terminal.Reason);
                var settled = await restarted.GetSnapshotAsync();
                Assert.Equal(RecoveryState.None, settled.Recovery);
                Assert.Equal(ProductionArmCause.PlcActivation, Assert.IsType<ProductionArmPolicyStatus>(
                    settled.ProductionArming).Cause);
                Assert.Equal(terminal.Kind == ProductionArmEventKind.ReadyConfirmed, settled.Ready);
                if (terminal.Kind == ProductionArmEventKind.ReadyConfirmed)
                    Assert.Equal(ProductionArmState.Armed, settled.ArmState);
                return;
            }
            finally
            {
                gate.Stop();
                assertion.Join(TimeSpan.FromSeconds(5));
                await restarted.DisposeAsync();
                await clockPump.DisposeAsync();
            }
        }
        Assert.Fail("The dedicated sample boundary could not be forced in three cold Runtimes: " +
            string.Join("; ", lostBoundary));
    }

    [Fact]
    [Trait("VerificationId", "V147_R18")]
    public async Task V147_R18_PendingStartUpCauseRefusesTheDedicatedPlcRequestWithoutActivation()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)),
            productionArming: new ProductionArmStoreOptions(),
            productionArmStatusBinding: V147ArmStatusBinding,
            startupProductionPolicy: V147AutomaticStartup(),
            productionArmMaintenance: new TestProductionArmMaintenanceProvider(),
            productionCommunicationPolicy: V147ArbitrationCommunicationPolicy());
        await WaitProductionAsync(harness, state => state.PlcCommunication is { Healthy: true },
            "Production communication did not synchronize");
        // The controller holds its own request input, so the automatic start-up attempt can never
        // complete its fresh-input window: it owns the Runtime exactly as it would while the line
        // is busy, and the whole case stays deterministic without touching Runtime state.
        peer.SetTrigger(true);
        try
        {
            var opened = await WaitForArmAttemptHistoryAsync(harness, page => page.Events.Any(entry =>
                    entry.Kind == ProductionArmEventKind.Attempted && entry.Cause == ProductionArmCause.Startup),
                "The start-up automatic attempt was not recorded");
            Assert.DoesNotContain(opened.Events, entry => entry.Cause == ProductionArmCause.PlcActivation);
            // The projected attempt is provably still open (Waiting) when the request arrives.
            await WaitProductionAsync(harness, state => state.ProductionArming is
                    { Cause: ProductionArmCause.Startup, Outcome: ProductionArmAttemptOutcome.Waiting },
                "The start-up automatic attempt was not projected as waiting");

            const uint sequence = 241;
            peer.RequestRecipeChange(sequence, V147ArbitrationSelectionCode);
            await WaitForDedicatedResponseAsync(peer, sequence, V147ArbitrationResponseTimeout);
            var response = peer.RecipeChangeResponse;
            // The fixed refusal is decided before any selection or activation work: the once-only
            // start-up cause already owns this Runtime.
            Assert.Equal((ushort)RecipeChangeOutcome.RejectedBusy, response.Outcome);
            Assert.Equal((ushort)RecipeChangeReason.RuntimeBusy, response.Reason);
            Assert.Equal(sequence, response.RequestSequence);
            Assert.Equal(V147ArbitrationSelectionCode, response.SelectionCode);

            await CompleteDedicatedEpisodeAsync(harness, peer, sequence);
            var query = new SqliteRecipeSelectionQuery(harness.Fixture.Options);
            var history = await query.QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
            Assert.True(history.Available, history.ReasonCode);
            Assert.Equal(new[] { RecipeChangeEventKind.RequestObserved, RecipeChangeEventKind.DecisionCommitted,
                    RecipeChangeEventKind.ResponsePublished, RecipeChangeEventKind.AcknowledgementObserved,
                    RecipeChangeEventKind.ResponseCleared, RecipeChangeEventKind.ResetObserved },
                history.Events.Where(value => value.Request.RequestSequence == sequence).Select(value => value.Kind));
            var decision = Assert.Single(history.Events, value =>
                value.Kind == RecipeChangeEventKind.DecisionCommitted &&
                value.Request.RequestSequence == sequence);
            Assert.Equal("ProductionArmAttemptInProgress", decision.ReasonCode);
            Assert.DoesNotContain(history.Events, value => value.Kind == RecipeChangeEventKind.ProtocolFault);

            // The start-up cause closes as the ordinary RuntimeBusy refusal of the whole attempt,
            // and no PLC activation cause was ever created for the refused request.
            var closed = await WaitForArmAttemptHistoryAsync(harness, page => page.Events.Any(entry =>
                    entry.Cause == ProductionArmCause.Startup && entry.Terminal),
                "The refused start-up attempt did not close");
            var attempted = Assert.Single(closed.Events, entry => entry.Kind == ProductionArmEventKind.Attempted);
            Assert.Equal(ProductionArmCause.Startup, attempted.Cause);
            var terminal = Assert.Single(closed.Events, entry => entry.Terminal &&
                entry.AttemptId == attempted.AttemptId);
            Assert.Equal(ProductionArmEventKind.Rejected, terminal.Kind);
            Assert.Equal(ProductionArmReason.RuntimeBusy, terminal.Reason);
            Assert.DoesNotContain(closed.Events, entry => entry.Cause == ProductionArmCause.PlcActivation);

            // No activation capability was reserved or spent, and the station never became ready.
            var activations = await harness.Activations.QueryAsync(new(PageSize: 20));
            Assert.True(activations.Available, activations.ReasonCode);
            Assert.DoesNotContain(activations.Records, value => value.Actor?.IsPlcAdapter == true);
            var state = await harness.Runtime.GetSnapshotAsync();
            Assert.Equal(RecoveryState.None, state.Recovery);
            Assert.False(state.Ready);
            Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
            Assert.False(peer.PhysicalProductionReady);
            Assert.Equal(0, peer.ProductionReadyWriteCount);
        }
        finally { peer.SetTrigger(false); }
    }

    /// <summary>
    /// Waits for the dedicated response block to carry the exact request identity. Waiting on the
    /// controller-owned observation keeps the acknowledgement and reset phases in their real
    /// order: an acknowledgement before the acknowledged response is a protocol fault, never a
    /// faster test.
    /// </summary>
    private static async Task WaitForDedicatedResponseAsync(ModbusQualificationTestServer peer, uint sequence,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!peer.RecipeChangeResponse.ResponseValid || peer.RecipeChangeResponse.RequestSequence != sequence)
        {
            if (DateTime.UtcNow >= deadline)
            {
                var state = peer.RecipeChangeResponse;
                throw new XunitException("The dedicated response for request " + sequence +
                    " was never published: valid=" + state.ResponseValid + " outcome=" + state.Outcome +
                    " reason=" + state.Reason + " sequence=" + state.RequestSequence);
            }
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Acknowledges and clears exactly the dedicated response, then waits for the durable reset
    /// observation. The helper deliberately reads the ledger instead of a Runtime snapshot: the
    /// case may run against a cold Runtime that replaced the harness owner.
    /// </summary>
    private static async Task CompleteDedicatedEpisodeAsync(ManualHarness harness, ModbusQualificationTestServer peer,
        uint sequence)
    {
        peer.AcknowledgeRecipeChange();
        await WaitConditionAsync(() => !peer.RecipeChangeResponse.ResponseValid,
            "The acknowledged dedicated response did not clear");
        peer.ResetRecipeChange();
        var query = new SqliteRecipeSelectionQuery(harness.Fixture.Options);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var history = await query.QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
            Assert.True(history.Available, history.ReasonCode);
            if (history.Events.Any(value => value.Request.RequestSequence == sequence &&
                value.Kind == RecipeChangeEventKind.ResetObserved)) return;
            if (DateTime.UtcNow >= deadline)
                throw new XunitException("The dedicated reset for request " + sequence + " was not observed");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Scheduling barrier for the once-only start-up consideration. The dedicated handshake
    /// initialization is the only place where the response block is read within two Modbus
    /// requests of the previous response-block read (controller block and response block back to
    /// back), while a synchronized sample spends at least five requests between two response-block
    /// reads. A dedicated spinning thread asserts the controller request at that exact delta, which
    /// is the last observable moment before the first production sample reads the same block. When
    /// the thread is descheduled the fallback fires after a whole second without a response read,
    /// which is a plainly late request: the caller rebuilds the cold Runtime instead of weakening
    /// an assertion. The gate reports which of the two paths fired, so a late request is never
    /// mistaken for a forced boundary, and it stops the thread as soon as the case decides.
    /// </summary>
    private static Thread StartRecipeChangeAtHandshakeInitialization(ModbusQualificationTestServer peer,
        uint sequence, uint code, V147AssertionGate gate)
    {
        var thread = new Thread(() =>
        {
            var previousCount = peer.RecipeChangeResponseReadCount;
            var previousRequests = peer.RequestCount;
            var changes = 0;
            var lastChange = Stopwatch.GetTimestamp();
            var deadline = lastChange + (long)(Stopwatch.Frequency * 60);
            while (!gate.Stopped && Stopwatch.GetTimestamp() < deadline)
            {
                var count = peer.RecipeChangeResponseReadCount;
                if (count == previousCount)
                {
                    if (changes > 0 && Stopwatch.GetTimestamp() - lastChange > Stopwatch.Frequency)
                    {
                        gate.Fired(boundary: false);
                        peer.RequestRecipeChange(sequence, code);
                        return;
                    }
                    Thread.SpinWait(50);
                    continue;
                }
                var requests = peer.RequestCount;
                var delta = requests - previousRequests;
                previousCount = count;
                previousRequests = requests;
                lastChange = Stopwatch.GetTimestamp();
                changes++;
                if (changes > 1 && delta <= 4)
                {
                    gate.Fired(boundary: true);
                    peer.RequestRecipeChange(sequence, code);
                    return;
                }
                Thread.SpinWait(50);
            }
            gate.Fired(boundary: false);
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "V147 dedicated request assertion"
        };
        thread.Start();
        return thread;
    }

    /// <summary>
    /// Reports how the scheduling barrier fired and lets the case stop a thread that never fired.
    /// </summary>
    private sealed class V147AssertionGate
    {
        private int _stopped;
        private int _fired = -1;

        internal void Stop() => Interlocked.Exchange(ref _stopped, 1);
        internal bool Stopped => Volatile.Read(ref _stopped) != 0;
        internal void Fired(bool boundary) => Interlocked.Exchange(ref _fired, boundary ? 1 : 0);
        internal bool BoundaryForced => Volatile.Read(ref _fired) == 1;
    }
}
