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
/// T47 cold-start Runtime coverage for the two governed production-arm causes. Every case composes a
/// brand-new StationRuntime against the reopened real schema-32 ledger, the real Modbus loopback
/// controller and the fixed admission engine. The fixture maintenance provider and the qualification
/// issuer are development-only consumers installed before the new owner starts; neither one can
/// qualify a deployment, fabricate CanArm or skip a gate.
///
/// V147_R14 records the actual cold-start boundary: a fresh process holds no live prepared algorithm
/// instance, so the single start-up attempt of an Automatic Start-Up policy is refused by the real
/// gate and never reaches Authorized, ReadyConfirmed or a physical Runtime-ready acknowledgement. The
/// test asserts that finding instead of mocking CanArm or skipping the gate: a first production arming
/// after a restart still requires a live in-process Recipe Activation and a human Manual Arm.
///
/// V147_R15 finalizes only the previous Runtime epoch's own retained attempt. It closes a pending
/// attempt as Failed/Interrupted, keeps an existing terminal kind unchanged, and adds the single
/// missing PlcStatusUndelivered disposition for both. No old-epoch physical status or Ready is
/// replayed, the verified read reports no retained obligation, and the same committed bytes re-verify.
/// </summary>
public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    [Trait("VerificationId", "V147_R14")]
    public async Task V147_R14_ColdStartupAutomaticArmIsRefusedByTheRealGateWithoutPhysicalReady()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer,
            productionArming: new ProductionArmStoreOptions(),
            startupProductionPolicy: V147AutomaticStartup());
        using var issuer = new ProductionTestIssuer();
        // The first process persists a real released recipe, PLC result contract and successful
        // activation; no Arm command is submitted, so the station stays physically disarmed.
        await PrepareProductionAsync(harness, issuer);
        var productionOptions = harness.Service<ProductionInspectionOptions>();
        var oldState = await harness.Runtime.GetSnapshotAsync();
        // No maintenance reader is attached to the first process, so its own start-up attempt is
        // refused before any admission work and can never write a physical Ready.
        var oldHistory = await WaitForArmAttemptHistoryAsync(harness, page => page.Events.Any(entry =>
            entry.Cause == ProductionArmCause.Startup && entry.StatusDelivery),
            "The first process did not close its own start-up attempt");
        Assert.Equal(0, peer.ProductionReadyWriteCount);
        Assert.False(peer.PhysicalProductionReady);

        await harness.StopRuntimePreservingFixtureAsync();
        await harness.Fixture.RestartStoreAsync();

        var clock = new VirtualCameraClock(DateTimeOffset.UtcNow);
        await using var clockPump = ClockPump.Start(clock);
        var provider = ManualHarness.CreateProvider(clock, timeout: false);
        var restarted = CreateRestartedManualRuntime(harness, new[] { provider }, clock);
        // Both evidence seams exist before the production owner starts: a start-up cause is
        // evaluated exactly once inside that owner, so a late provider could never be observed.
        var maintenance = new TestProductionArmMaintenanceProvider().Attach(
            harness.Fixture.Options.LocalIdentity!.StationId, harness.Deployment!.ContentHash,
            new string('A', 64), ProductionArmMaintenanceState.ManualArmConfirmed);
        restarted.ConfigureProductionInspectionQualificationEvidenceProvider(issuer);
        restarted.ConfigureProductionArmMaintenanceEvidenceProvider(maintenance);
        restarted.ConfigureProductionInspections(productionOptions, harness.Fixture.Options,
            harness.Service<AlgorithmExecutionOptions>(), clock);
        try
        {
            await restarted.WaitForRecipeActivationStartupAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await restarted.WaitForManualInspectionStartupAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var cold = await WaitColdProductionStateAsync(restarted, state => state.ProductionArming is
                { Cause: ProductionArmCause.Startup, Outcome: ProductionArmAttemptOutcome.Rejected },
                "The cold start-up automatic attempt did not close as Rejected");
            var status = Assert.IsType<ProductionArmPolicyStatus>(cold.ProductionArming);
            var coldEpoch = cold.RuntimeEpoch;
            Assert.NotEqual(oldState.RuntimeEpoch, coldEpoch);
            Assert.Equal(coldEpoch, status.RuntimeEpoch);
            Assert.Equal(harness.Deployment!.StartupProduction.Reference, status.StartupPolicy);
            Assert.Equal(harness.Deployment.PostActivationArm.Reference, status.PostActivationPolicy);
            Assert.Equal(RecoveryState.None, cold.Recovery);
            // The persisted Active recipe is real, but a fresh process has not prepared it.
            Assert.NotNull(cold.ActiveRecipe);
            Assert.Contains("ActiveRecipePreparationRequired", cold.AdmissionBlockers);
            Assert.False(cold.Ready);
            Assert.Equal(ProductionArmState.Disarmed, cold.ArmState);
            // Refused by the real engine, never by a mocked CanArm: the report below keeps the two
            // live-preparation gates unpassed, and both the gate-blocked refusal and a post-capture
            // authority change reduce to the same outcome here (no arm, no Ready).
            Assert.True(status.Reason is ProductionArmReason.AdmissionGateBlocked or
                ProductionArmReason.AuthorityChanged, status.ReasonCode);

            var history = await WaitForArmAttemptHistoryAsync(harness, page => page.Events.Any(entry =>
                entry.AttemptId == status.AttemptId && entry.StatusDelivery),
                "The refused cold start-up attempt has no status disposition");
            var attempt = history.Events.Where(entry => entry.AttemptId == status.AttemptId).ToArray();
            var attempted = Assert.Single(attempt, entry => entry.Kind == ProductionArmEventKind.Attempted);
            var terminal = Assert.Single(attempt, entry => entry.Terminal);
            Assert.Equal(ProductionArmEventKind.Rejected, terminal.Kind);
            Assert.NotEqual(ProductionArmReason.None, terminal.Reason);
            // The report is the real fixed gate set captured by the engine in this new process.
            Assert.False(terminal.Report!.CanArm);
            Assert.Contains(terminal.Report.Gates, gate => gate.Gate == ProductionAdmissionGate.PreparedAlgorithm &&
                gate.Status != ProductionAdmissionGateStatus.Passed);
            Assert.Contains(terminal.Report.Gates, gate => gate.Gate == ProductionAdmissionGate.ProductionCycle &&
                gate.Status != ProductionAdmissionGateStatus.Passed);
            Assert.Equal(coldEpoch, attempted.RuntimeEpoch);
            Assert.Equal(coldEpoch, terminal.RuntimeEpoch);
            Assert.Equal(harness.Fixture.Options.LocalIdentity!.StationId, attempted.StationId);
            Assert.Equal(harness.Deployment.ContentHash, attempted.DeploymentHash);
            Assert.Equal(harness.Deployment.StartupProduction.Reference, attempted.StartupPolicy);
            Assert.Equal(harness.Deployment.PostActivationArm.Reference, attempted.PostActivationPolicy);
            Assert.Equal(maintenance.Current!.JournalHeadHash, attempted.MaintenanceHeadHash);
            Assert.True(attempted.RuntimeActor);
            Assert.Null(attempted.HumanPrincipalId);
            Assert.Null(attempted.PlcRequest);
            // Exactly one start-up attempt exists for the new epoch, and none was authorized.
            Assert.Single(history.Events, entry => entry.Kind == ProductionArmEventKind.Attempted &&
                entry.RuntimeEpoch == coldEpoch);
            Assert.Equal(new[] { ProductionArmEventKind.Attempted, ProductionArmEventKind.Rejected,
                ProductionArmEventKind.PlcStatusUndelivered }, attempt.Select(entry => entry.Kind));
            Assert.DoesNotContain(attempt, entry => entry.Kind is ProductionArmEventKind.Authorized or
                ProductionArmEventKind.ReadyConfirmed or ProductionArmEventKind.PlcStatusDelivered);
            // No physical Runtime-ready acknowledgement ever reached the controller.
            Assert.Equal(0, peer.ProductionReadyWriteCount);
            Assert.False(peer.PhysicalProductionReady);
            // Without a deployed status block no attempt publishes a status observation, and the
            // Runtime never claims a delivery it did not make.
            Assert.Empty(peer.ProductionArmStatusWrites);
            Assert.False(status.PlcStatusDelivered);
            Assert.Equal(ProductionArmReason.ConfigurationUnavailable,
                Assert.Single(attempt, entry => entry.StatusDelivery).Reason);
            // The single disposition leaves no open obligation behind, and the earlier epoch's
            // already-closed start-up attempt was not rewritten by this process.
            var read = await new SqliteProductionArmHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
            Assert.True(read.Available, read.ReasonCode);
            Assert.False(read.RecoveryRequired);
            Assert.Null(read.Pending);
            Assert.Equal(status.AttemptId, read.Current!.AttemptId);
            Assert.True(read.Current.StatusDelivery);
            var oldEvents = history.Events.Where(entry =>
                entry.RuntimeEpoch == oldState.RuntimeEpoch).ToArray();
            Assert.Equal(oldHistory.Events.Where(entry => entry.RuntimeEpoch == oldState.RuntimeEpoch)
                .Select(entry => entry.ContentHash), oldEvents.Select(entry => entry.ContentHash));
        }
        finally
        {
            await restarted.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("VerificationId", "V147_R15")]
    public async Task V147_R15_ColdRestartDisposesThePreviousEpochAttemptWithoutReplayingPhysicalState(
        bool terminalPresent)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer, productionArming: new ProductionArmStoreOptions());
        var deployment = harness.Deployment!;
        var stationId = harness.Fixture.Options.LocalIdentity!.StationId;
        var oldEpoch = (await harness.Runtime.GetSnapshotAsync()).RuntimeEpoch;

        // The previous Runtime stopped while its own start-up attempt was still retained. The rows
        // are written through the real schema-32 writer lane (which re-verifies the signed chain)
        // with a legitimate start-up cause and no PLC request, activation, maintenance head or human
        // attribution: no physical PLC activation is claimed here.
        await harness.StopRuntimePreservingFixtureAsync();
        var attemptId = Guid.NewGuid();
        var attempted = await harness.Fixture.Store.AppendProductionArmEventAsync(
            new ProductionArmWriteRequest(attemptId, oldEpoch, ProductionArmCause.Startup,
                ProductionArmEventKind.Attempted, stationId, deployment.StartupProduction.Reference,
                deployment.PostActivationArm.Reference, deployment.ContentHash, null, null, null, 0, null,
                new Dictionary<string, string>(), new Dictionary<string, string>(),
                ProductionArmReason.None, "ProductionArmAttemptStarted"),
            new StoreDeadline(TimeSpan.FromSeconds(5)), CancellationToken.None);
        Assert.True(attempted.Committed, attempted.ReasonCode);
        if (terminalPresent)
        {
            var rejected = await harness.Fixture.Store.AppendProductionArmEventAsync(
                new ProductionArmWriteRequest(attemptId, oldEpoch, ProductionArmCause.Startup,
                    ProductionArmEventKind.Rejected, stationId, deployment.StartupProduction.Reference,
                    deployment.PostActivationArm.Reference, deployment.ContentHash, null, null, null, 0, null,
                    new Dictionary<string, string>(), new Dictionary<string, string>(),
                    ProductionArmReason.AdmissionGateBlocked, "ProductionArmAdmissionRejected"),
                new StoreDeadline(TimeSpan.FromSeconds(5)), CancellationToken.None);
            Assert.True(rejected.Committed, rejected.ReasonCode);
        }

        await harness.Fixture.RestartStoreAsync();
        var clock = new VirtualCameraClock(DateTimeOffset.UtcNow);
        await using var clockPump = ClockPump.Start(clock);
        var provider = ManualHarness.CreateProvider(clock, timeout: false);
        var restarted = CreateRestartedManualRuntime(harness, new[] { provider }, clock);
        restarted.ConfigureProductionInspections(harness.Service<ProductionInspectionOptions>(),
            harness.Fixture.Options, harness.Service<AlgorithmExecutionOptions>(), clock);
        try
        {
            await restarted.WaitForRecipeActivationStartupAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await restarted.WaitForManualInspectionStartupAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var history = await WaitForArmAttemptHistoryAsync(harness, page => page.Events.Any(entry =>
                entry.AttemptId == attemptId && entry.StatusDelivery),
                "The cold Runtime did not dispose the previous-epoch attempt");
            var cold = await WaitColdProductionStateAsync(restarted,
                state => state.Recovery == RecoveryState.None,
                "The cold Runtime did not finish its own startup");
            Assert.NotEqual(oldEpoch, cold.RuntimeEpoch);
            var events = history.Events.Where(entry => entry.AttemptId == attemptId).ToArray();
            if (terminalPresent)
            {
                // A terminal state is never repurposed: only the missing disposition was added.
                Assert.Equal(new[] { ProductionArmEventKind.Attempted, ProductionArmEventKind.Rejected,
                    ProductionArmEventKind.PlcStatusUndelivered }, events.Select(entry => entry.Kind));
                Assert.Equal(ProductionArmReason.AdmissionGateBlocked, events[1].Reason);
                Assert.Equal("ProductionArmAdmissionRejected", events[1].ReasonCode);
            }
            else
            {
                // A pending attempt is closed by the new Runtime itself, with its own reason.
                Assert.Equal(new[] { ProductionArmEventKind.Attempted, ProductionArmEventKind.Failed,
                    ProductionArmEventKind.PlcStatusUndelivered }, events.Select(entry => entry.Kind));
                Assert.Equal(ProductionArmReason.Interrupted, events[1].Reason);
                Assert.Equal("ProductionArmPreviousRuntimeInterrupted", events[1].ReasonCode);
            }
            Assert.Equal(ProductionArmReason.ConfigurationUnavailable, events[2].Reason);
            Assert.Equal("ProductionArmPreviousRuntimeStatusUnconfirmed", events[2].ReasonCode);
            Assert.Null(events[2].ReadyReceipt);
            Assert.All(events, entry =>
            {
                Assert.Equal(oldEpoch, entry.RuntimeEpoch);
                Assert.Equal(stationId, entry.StationId);
                Assert.Equal(deployment.ContentHash, entry.DeploymentHash);
                Assert.True(entry.RuntimeActor);
                Assert.Null(entry.HumanPrincipalId);
                Assert.Null(entry.PlcRequest);
                Assert.Null(entry.Activation);
            });
            Assert.DoesNotContain(events, entry => entry.Kind is ProductionArmEventKind.Authorized or
                ProductionArmEventKind.ReadyConfirmed or ProductionArmEventKind.PlcStatusDelivered);
            // Nothing old was resent to the controller and the cold Runtime never armed.
            Assert.Equal(0, peer.ProductionReadyWriteCount);
            Assert.False(peer.PhysicalProductionReady);
            Assert.DoesNotContain(peer.ProductionArmStatusWrites, write => write.AttemptId == attemptId);
            Assert.DoesNotContain(peer.ProductionArmStatusWrites, write => write.PhysicalProductionReady);
            Assert.Null(cold.ProductionArming);
            Assert.False(cold.Ready);
            Assert.Equal(ProductionArmState.Disarmed, cold.ArmState);
            Assert.DoesNotContain(history.Events, entry => entry.Kind == ProductionArmEventKind.Attempted &&
                entry.RuntimeEpoch == cold.RuntimeEpoch);
            // The verified page ends at the single disposition row: nothing else was appended.
            Assert.Equal(3L, history.ThroughPosition);
            // A fresh verified read re-proves the retained previous-epoch ledger, and the released
            // tail leaves no pending attempt or recovery requirement behind.
            var read = await new SqliteProductionArmHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
            Assert.True(read.Available, read.ReasonCode);
            Assert.False(read.RecoveryRequired);
            Assert.Null(read.Pending);
            Assert.Equal(attemptId, read.Current!.AttemptId);
            Assert.Equal(ProductionArmEventKind.PlcStatusUndelivered, read.Current.Kind);
        }
        finally
        {
            await restarted.DisposeAsync();
        }
    }

    /// <summary>
    /// Waits for one exact cold-owner projection. The failure message names the durable recovery,
    /// arm, attempt and admission observations so a refusal can never be mistaken for a Ready.
    /// </summary>
    private static async Task<StationStateSnapshot> WaitColdProductionStateAsync(StationRuntime runtime,
        Func<StationStateSnapshot, bool> predicate, string reason)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        StationStateSnapshot? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await runtime.GetSnapshotAsync();
            if (predicate(last)) return last;
            await Task.Delay(25);
        }
        throw new XunitException(reason + ": " + last?.Recovery + "/" + last?.Ready + "/" + last?.ArmState + "/" +
            last?.ProductionArming?.Outcome + "/" + last?.ProductionArming?.ReasonCode + "/blockers=" +
            string.Join(",", last?.AdmissionBlockers.AsEnumerable() ?? Array.Empty<string>()));
    }
}
