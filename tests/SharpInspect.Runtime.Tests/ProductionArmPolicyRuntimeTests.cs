using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// T47 Runtime integration coverage for the two governed production-arm policies. Every case
/// composes the real StationRuntime, the real schema-32 arm ledger, the real qualification
/// issuer (ProductionTestIssuer) and the controller-owned Modbus peer. The fixture
/// maintenance-evidence provider is an explicitly development-only stand-in: it exercises the
/// consumer, never qualifies a deployment and never stands in for a production maintenance
/// journal reader.
/// </summary>
public sealed partial class ManualInspectionRuntimeTests
{
    private static readonly ModbusProductionArmStatusBinding V147ArmStatusBinding =
        new("V147.Modbus.ProductionArmStatus", "1", 700);

    private static StartupProductionPolicy V147AutomaticStartup() =>
        new("V147.StartupProduction", "1", StartupProductionMode.AutomaticArm);

    private static PostActivationArmPolicy V147AutomaticRearm() =>
        new("V147.PostActivationArm", "1", PostActivationArmMode.AutomaticRearmAfterPlcActivation);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task V147_R09_AutomaticPostActivationRequiresBothHandshakeAndStatusBindings(
        bool handshakePresent, bool statusPresent)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer, productionArming: new ProductionArmStoreOptions(),
            postActivationArmPolicy: V147AutomaticRearm(),
            recipeChangeBinding: handshakePresent ? new(500, 600, TimeSpan.FromSeconds(20)) : null,
            productionArmStatusBinding: statusPresent ? V147ArmStatusBinding : null));
        Assert.Equal("ProductionArmPlcHandshakeAndStatusConfigurationRequired", error.Message);
        Assert.False(peer.PhysicalProductionReady);
    }

    [Theory]
    [InlineData(StartupProductionMode.ManualArm, PostActivationArmMode.ManualRearm)]
    [InlineData(StartupProductionMode.ManualArm, PostActivationArmMode.AutomaticRearmAfterPlcActivation)]
    [InlineData(StartupProductionMode.AutomaticArm, PostActivationArmMode.ManualRearm)]
    [InlineData(StartupProductionMode.AutomaticArm, PostActivationArmMode.AutomaticRearmAfterPlcActivation)]
    public async Task V147_R01_OnlyTheStartupChoiceSelectsAStartUpAttempt(
        StartupProductionMode startup, PostActivationArmMode post)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer,
            productionArming: new ProductionArmStoreOptions(),
            startupProductionPolicy: new StartupProductionPolicy("V147.StartupProduction", "1", startup),
            postActivationArmPolicy: new PostActivationArmPolicy("V147.PostActivationArm", "1", post),
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)),
            productionArmStatusBinding: V147ArmStatusBinding,
            productionArmMaintenance: new TestProductionArmMaintenanceProvider());
        var query = new SqliteProductionArmHistoryQuery(harness.Fixture.Options);
        if (startup == StartupProductionMode.ManualArm)
        {
            // The deployment default is Manual Arm: declaring an automatic post-activation
            // policy neither selects an attempt nor moves the start-up choice.
            await Task.Delay(TimeSpan.FromMilliseconds(800));
            var page = await query.QueryAsync(new ProductionArmHistoryFilter(PageSize: 128));
            Assert.True(page.Available, page.ReasonCode);
            Assert.Empty(page.Events);
            Assert.Null((await harness.Runtime.GetSnapshotAsync()).ProductionArming);
            Assert.Equal("V147.PostActivationArm", harness.Deployment!.PostActivationArm.Id);
            Assert.Equal(StartupProductionMode.ManualArm, harness.Deployment!.StartupProduction.Mode);
            return;
        }

        var history = await WaitForArmAttemptHistoryAsync(harness,
            value => value.Events.Any(entry => entry.Cause == ProductionArmCause.Startup && entry.Terminal),
            "Automatic start-up attempt was not recorded and closed");
        var attempted = Assert.Single(history.Events,
            entry => entry.Kind == ProductionArmEventKind.Attempted && entry.Cause == ProductionArmCause.Startup);
        var terminal = Assert.Single(history.Events,
            entry => entry.Terminal && entry.AttemptId == attempted.AttemptId);
        Assert.Equal(ProductionArmEventKind.Rejected, terminal.Kind);
        Assert.NotEqual(ProductionArmReason.None, terminal.Reason);
        Assert.Equal(harness.Deployment!.StartupProduction.Reference, attempted.StartupPolicy);
        Assert.Equal(harness.Deployment!.PostActivationArm.Reference, attempted.PostActivationPolicy);
        Assert.Equal(harness.Deployment!.ContentHash, attempted.DeploymentHash);
        Assert.True(attempted.RuntimeActor);
        Assert.Null(attempted.HumanPrincipalId);

        await WaitProductionAsync(harness, value => value.ProductionArming is
            { Cause: ProductionArmCause.Startup, Outcome: ProductionArmAttemptOutcome.Rejected },
            "Automatic start-up attempt did not close as Rejected");
        var state = await harness.Runtime.GetSnapshotAsync();
        var arming = state.ProductionArming!;
        Assert.Equal(ProductionArmCause.Startup, arming.Cause);
        Assert.Equal(ProductionArmAttemptOutcome.Rejected, arming.Outcome);
        Assert.Equal(attempted.AttemptId, arming.AttemptId);
        Assert.False(state.Ready);
        Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
        Assert.False(peer.PhysicalProductionReady);
    }

    [Fact]
    public async Task V147_R02_MissingMaintenanceEvidenceIsOneShotAndNeverRetried()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer,
            productionArming: new ProductionArmStoreOptions(),
            startupProductionPolicy: V147AutomaticStartup());

        // No production maintenance reader exists, so the default observation must deny.
        var history = await WaitForArmAttemptHistoryAsync(harness,
            value => value.Events.Any(entry => entry.StatusDelivery),
            "Automatic start-up attempt never closed without maintenance evidence");
        var attempted = Assert.Single(history.Events,
            entry => entry.Kind == ProductionArmEventKind.Attempted && entry.Cause == ProductionArmCause.Startup);
        var terminal = Assert.Single(history.Events,
            entry => entry.Terminal && entry.AttemptId == attempted.AttemptId);
        Assert.Equal(ProductionArmEventKind.Rejected, terminal.Kind);
        Assert.Equal(ProductionArmReason.MaintenanceEvidenceUnavailable, terminal.Reason);
        await WaitProductionAsync(harness, value => value.ProductionArming is
            { Cause: ProductionArmCause.Startup, Outcome: ProductionArmAttemptOutcome.Rejected },
            "Start-up attempt without maintenance did not close as Rejected");
        var denied = await harness.Runtime.GetSnapshotAsync();
        var deniedArming = denied.ProductionArming!;
        Assert.Equal(ProductionArmAttemptOutcome.Rejected, deniedArming.Outcome);
        Assert.Equal(ProductionArmReason.MaintenanceEvidenceUnavailable, deniedArming.Reason);
        Assert.Equal(attempted.AttemptId, deniedArming.AttemptId);
        Assert.False(denied.Ready);

        // Presenting maintenance evidence afterwards cannot re-open a decision already made.
        var maintenance = new TestProductionArmMaintenanceProvider().Attach(
            harness.Fixture.Options.LocalIdentity!.StationId, harness.Deployment!.ContentHash,
            new string('B', 64), ProductionArmMaintenanceState.ManualArmConfirmed);
        var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
        runtime.ConfigureProductionArmMaintenanceEvidenceProvider(maintenance);
        await runtime.RefreshProductionAdmissionAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(800));

        var after = await new SqliteProductionArmHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new ProductionArmHistoryFilter(PageSize: 128));
        Assert.True(after.Available, after.ReasonCode);
        Assert.Equal(history.Events.Count, after.Events.Count);
        Assert.All(after.Events, entry => Assert.Equal(attempted.AttemptId, entry.AttemptId));
        var state = await harness.Runtime.GetSnapshotAsync();
        var afterArming = state.ProductionArming!;
        Assert.Equal(ProductionArmAttemptOutcome.Rejected, afterArming.Outcome);
        Assert.Equal(ProductionArmReason.MaintenanceEvidenceUnavailable, afterArming.Reason);
        Assert.Equal(attempted.AttemptId, afterArming.AttemptId);
        Assert.False(state.Ready);
    }

    [Fact]
    public async Task V147_R03_ConfirmedMaintenanceNeverBypassesProductionAdmission()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer,
            productionArming: new ProductionArmStoreOptions(),
            startupProductionPolicy: V147AutomaticStartup(),
            productionArmMaintenance: new TestProductionArmMaintenanceProvider());

        // Maintenance is confirmed, but the station has no released, activated recipe and no
        // qualification evidence. The attempt must still refuse and must never reach Ready.
        var history = await WaitForArmAttemptHistoryAsync(harness,
            value => value.Events.Any(entry => entry.Terminal && entry.Cause == ProductionArmCause.Startup),
            "Automatic start-up attempt never closed with confirmed maintenance");
        var terminal = Assert.Single(history.Events,
            entry => entry.Terminal && entry.Cause == ProductionArmCause.Startup);
        Assert.Equal(ProductionArmEventKind.Rejected, terminal.Kind);
        Assert.Equal(ProductionArmReason.AdmissionGateBlocked, terminal.Reason);
        Assert.DoesNotContain(history.Events, entry => entry.Kind == ProductionArmEventKind.Authorized);
        await WaitProductionAsync(harness, value => value.ProductionArming is
            { Cause: ProductionArmCause.Startup, Outcome: ProductionArmAttemptOutcome.Rejected },
            "Start-up attempt with confirmed maintenance did not close as Rejected");
        var state = await harness.Runtime.GetSnapshotAsync();
        var arming = state.ProductionArming!;
        Assert.Equal(ProductionArmAttemptOutcome.Rejected, arming.Outcome);
        Assert.Equal(ProductionArmReason.AdmissionGateBlocked, arming.Reason);
        Assert.False(state.Ready);
        Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
        Assert.False(peer.PhysicalProductionReady);
    }

    [Fact]
    public async Task V147_R04_PostActivationRearmReachesReadyOnlyAfterTheActualPhysicalWrite()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)),
            productionArming: new ProductionArmStoreOptions(),
            productionArmStatusBinding: V147ArmStatusBinding,
            postActivationArmPolicy: V147AutomaticRearm(),
            productionArmMaintenance: new TestProductionArmMaintenanceProvider());
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!;
        await EnableRecipeChangeAsync(harness, previous);
        await ArmProductionAsync(harness);
        await WaitConditionAsync(() => peer.PhysicalProductionReady, "Physical Ready before mapped activation");

        var sequence = await RequestRecipeChangeWithFreshBusyAttemptsAsync(harness, peer, 141);
        await WaitRecipeResponseAsync(harness, peer);
        Assert.Equal((ushort)RecipeChangeOutcome.Succeeded, peer.RecipeChangeResponse.Outcome);
        await CompleteRecipeChangeAsync(harness, peer, new SqliteRecipeSelectionQuery(harness.Fixture.Options));

        await WaitProductionAsync(harness, state => state.Ready && state.ProductionArming is
            { Cause: ProductionArmCause.PlcActivation, Outcome: ProductionArmAttemptOutcome.ReadyConfirmed, PlcStatusDelivered: true },
            "Post-activation automatic rearm did not confirm physical Ready");
        var state = await harness.Runtime.GetSnapshotAsync();
        var status = Assert.IsType<ProductionArmPolicyStatus>(state.ProductionArming);
        Assert.True(state.Ready);
        Assert.Equal(ProductionArmState.Armed, state.ArmState);
        Assert.Equal(harness.Deployment!.StartupProduction.Reference, status.StartupPolicy);
        Assert.Equal(harness.Deployment!.PostActivationArm.Reference, status.PostActivationPolicy);
        Assert.Equal(sequence, status.RequestSequence);
        Assert.Equal(7u, status.SelectionCode);
        var activated = Assert.IsType<RecipeActivationRecord>(
            (await harness.Activations.ReadCurrentAsync()).Record);
        Assert.True(activated.Actor!.IsPlcAdapter);
        Assert.True(activated.Outcome.Succeeded);

        // The observation block is a decoded wire value, not a Runtime echo.
        var wire = await WaitForArmStatusWriteAsync(peer, status.AttemptId);
        Assert.True(wire.Valid);
        Assert.Equal((ushort)ProductionArmAttemptOutcome.ReadyConfirmed, wire.Outcome);
        Assert.Equal((ushort)ProductionArmCause.PlcActivation, wire.Cause);
        Assert.Equal((ushort)ProductionArmReason.None, wire.Reason);
        Assert.Equal((ushort)0, wire.BlockedGate);
        Assert.Equal(status.AttemptId, wire.AttemptId);
        Assert.Equal(status.RuntimeEpoch, wire.RuntimeEpoch);
        Assert.Equal(status.ControllerEpoch, wire.ControllerEpoch);
        Assert.Equal(sequence, wire.RequestSequence);
        Assert.Equal(7u, wire.SelectionCode);
        Assert.Equal(V147ArmStatusBinding.RuntimeStartAddress, wire.StartAddress);
        // The Ready observation exists only because the physical Runtime-ready write
        // had already reached the controller.
        Assert.True(wire.PhysicalProductionReady,
            "Production arm status was published before the physical Ready write was served");
        Assert.True(peer.PhysicalProductionReady);
        Assert.True(peer.ProductionReadyWriteCount >= 1);

        var history = await WaitForArmAttemptHistoryAsync(harness, page => page.Events.Any(entry =>
            entry.AttemptId == status.AttemptId && entry.Kind == ProductionArmEventKind.PlcStatusDelivered),
            "Confirmed attempt has no delivered status observation");
        var events = history.Events.Where(entry => entry.AttemptId == status.AttemptId).ToArray();
        Assert.Equal(new[] { ProductionArmEventKind.Attempted, ProductionArmEventKind.Authorized,
            ProductionArmEventKind.ReadyConfirmed, ProductionArmEventKind.PlcStatusDelivered },
            events.Select(entry => entry.Kind));
        var authorized = events[1];
        Assert.NotNull(authorized.Report);
        Assert.True(authorized.Report!.CanArm);
        Assert.Equal(status.RuntimeEpoch, authorized.RuntimeEpoch);
        Assert.Equal(harness.Deployment!.ContentHash, authorized.DeploymentHash);
        Assert.NotNull(authorized.PlcRequest);
        Assert.Equal(sequence, authorized.PlcRequest!.RequestSequence);
        Assert.NotNull(authorized.Activation);
        Assert.True(ProductionArmHeads.Equal(authorized.ExpectedDurableHeads, authorized.CurrentDurableHeads));
        Assert.True(authorized.RuntimeActor);
        var confirmed = events[2];
        Assert.Equal(harness.Maintenance!.Current!.JournalHeadHash, confirmed.MaintenanceHeadHash);
        Assert.Equal(ProductionArmReason.None, events[3].Reason);
        // The confirmed rearm stays Ready after its status observation; no later
        // audit or admission recheck may silently revoke the acknowledged Ready.
        var settled = await harness.Runtime.GetSnapshotAsync();
        Assert.True(settled.Ready);
        Assert.Equal(ProductionArmState.Armed, settled.ArmState);
    }

    [Theory]
    [InlineData(true, true, (int)ProductionArmMaintenanceState.ManualArmRequired,
        ProductionArmReason.ManualArmAfterMaintenanceRequired)]
    [InlineData(false, true, (int)ProductionArmMaintenanceState.ManualArmConfirmed,
        ProductionArmReason.MaintenanceEvidenceUnavailable)]
    public async Task V147_R05_PostActivationRefusalsStayDisarmedAndNeverResurrectTheHandshakeResponse(
        bool maintenanceProviderPresent, bool statusBindingPresent, int maintenanceState,
        ProductionArmReason expectedReason)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)),
            productionArming: new ProductionArmStoreOptions(),
            productionArmStatusBinding: statusBindingPresent ? V147ArmStatusBinding : null,
            postActivationArmPolicy: V147AutomaticRearm(),
            productionArmMaintenance: maintenanceProviderPresent ? new TestProductionArmMaintenanceProvider() : null,
            productionArmMaintenanceState: (ProductionArmMaintenanceState)maintenanceState);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!;
        await EnableRecipeChangeAsync(harness, previous);
        // No human Arm here: the refusal must hold on the exact maintenance observation
        // alone, without depending on any manual maintenance arm episode.

        var sequence = await RequestRecipeChangeWithFreshBusyAttemptsAsync(harness, peer, 151);
        await WaitRecipeResponseAsync(harness, peer);
        Assert.Equal((ushort)RecipeChangeOutcome.Succeeded, peer.RecipeChangeResponse.Outcome);
        await CompleteRecipeChangeAsync(harness, peer, new SqliteRecipeSelectionQuery(harness.Fixture.Options));

        var history = await WaitForArmAttemptHistoryAsync(harness, value => value.Events.Any(entry =>
            entry.Cause == ProductionArmCause.PlcActivation && entry.Terminal),
            "Refused post-activation attempt was not recorded");
        var attempted = Assert.Single(history.Events, entry =>
            entry.Kind == ProductionArmEventKind.Attempted && entry.Cause == ProductionArmCause.PlcActivation);
        var terminal = Assert.Single(history.Events, entry => entry.Terminal && entry.AttemptId == attempted.AttemptId);
        Assert.Equal(ProductionArmEventKind.Rejected, terminal.Kind);
        Assert.Equal(expectedReason, terminal.Reason);
        Assert.DoesNotContain(history.Events, entry =>
            entry.AttemptId == attempted.AttemptId && entry.Kind == ProductionArmEventKind.Authorized);
        Assert.DoesNotContain(history.Events, entry =>
            entry.AttemptId == attempted.AttemptId && entry.Kind == ProductionArmEventKind.ReadyConfirmed);
        Assert.Equal(sequence, attempted.PlcRequest!.RequestSequence);
        Assert.Equal(7u, attempted.PlcRequest.SelectionCode);

        await WaitProductionAsync(harness, state => state.ProductionArming is
            { Cause: ProductionArmCause.PlcActivation, Outcome: ProductionArmAttemptOutcome.ActivatedButNotReady },
            "Refused post-activation attempt did not project ActivatedButNotReady");
        var state = await harness.Runtime.GetSnapshotAsync();
        var status = Assert.IsType<ProductionArmPolicyStatus>(state.ProductionArming);
        Assert.Equal(expectedReason, status.Reason);
        Assert.Equal(attempted.AttemptId, status.AttemptId);
        Assert.False(state.Ready);
        Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
        Assert.False(peer.PhysicalProductionReady);
        // The completed dedicated handshake keeps its all-zero reset; a refused rearm
        // never publishes another response.
        Assert.False(peer.RecipeChangeResponse.ResponseValid);
        Assert.Equal((ushort)0, peer.RecipeChangeResponse.Outcome);
        Assert.Equal((ushort)0, peer.RecipeChangeResponse.Reason);
        Assert.Equal(0u, peer.RecipeChangeResponse.RequestSequence);
        Assert.Equal(0u, peer.RecipeChangeResponse.SelectionCode);

        if (!statusBindingPresent)
        {
            // Without the required observation block the PLC path cannot publish a status,
            // and the refusal is still durable in the Runtime projection and the ledger.
            Assert.Empty(peer.ProductionArmStatusWrites);
            return;
        }
        var armStatus = await WaitForArmStatusWriteAsync(peer, status.AttemptId);
        Assert.True(armStatus.Valid);
        Assert.Equal((ushort)ProductionArmCause.PlcActivation, armStatus.Cause);
        Assert.Equal((ushort)ProductionArmAttemptOutcome.ActivatedButNotReady, armStatus.Outcome);
        Assert.Equal((ushort)expectedReason, armStatus.Reason);
        Assert.Equal((ushort)0, armStatus.BlockedGate);
        Assert.Equal(status.RuntimeEpoch, armStatus.RuntimeEpoch);
        Assert.Equal(status.ControllerEpoch, armStatus.ControllerEpoch);
        Assert.Equal(sequence, armStatus.RequestSequence);
        Assert.Equal(7u, armStatus.SelectionCode);
        Assert.False(armStatus.PhysicalProductionReady);
    }

    [Fact]
    public async Task V147_R06_HeldControllerRequestTimesOutAsOneAttemptWithoutReady()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        var policy = new PlcCommunicationPolicy("V147.ArmInputs", "1", TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(40), 2,
            TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(2));
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)),
            productionCommunicationPolicy: policy,
            productionArming: new ProductionArmStoreOptions(),
            productionArmStatusBinding: V147ArmStatusBinding,
            postActivationArmPolicy: V147AutomaticRearm(),
            productionArmMaintenance: new TestProductionArmMaintenanceProvider());
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!;
        await EnableRecipeChangeAsync(harness, previous);
        await ArmProductionAsync(harness);
        await WaitConditionAsync(() => peer.PhysicalProductionReady, "Physical Ready before mapped activation");
        var writesBeforeAttempt = peer.ProductionReadyWriteCount;

        var sequence = await RequestRecipeChangeWithFreshBusyAttemptsAsync(harness, peer, 161);
        await WaitRecipeResponseAsync(harness, peer);
        Assert.Equal((ushort)RecipeChangeOutcome.Succeeded, peer.RecipeChangeResponse.Outcome);
        await CompleteRecipeChangeAsync(harness, peer, new SqliteRecipeSelectionQuery(harness.Fixture.Options));
        // The controller holds a production request high across the whole freshness window.
        peer.SetTrigger(true);

        var history = await WaitForArmAttemptHistoryAsync(harness, value => value.Events.Any(entry =>
            entry.Cause == ProductionArmCause.PlcActivation && entry.Terminal),
            "Held controller request never timed the freshness window out");
        var attempted = Assert.Single(history.Events, entry =>
            entry.Kind == ProductionArmEventKind.Attempted && entry.Cause == ProductionArmCause.PlcActivation);
        var terminal = Assert.Single(history.Events, entry => entry.Terminal && entry.AttemptId == attempted.AttemptId);
        Assert.Equal(ProductionArmEventKind.Rejected, terminal.Kind);
        Assert.Equal(ProductionArmReason.InputsNotStable, terminal.Reason);
        Assert.Single(history.Events, entry =>
            entry.Kind == ProductionArmEventKind.Attempted && entry.Cause == ProductionArmCause.PlcActivation);
        Assert.DoesNotContain(history.Events, entry =>
            entry.AttemptId == attempted.AttemptId && entry.Kind == ProductionArmEventKind.Authorized);
        Assert.Equal(sequence, attempted.PlcRequest!.RequestSequence);

        await WaitProductionAsync(harness, value => value.ProductionArming is
            { Cause: ProductionArmCause.PlcActivation, Outcome: ProductionArmAttemptOutcome.ActivatedButNotReady },
            "Held controller request did not close as ActivatedButNotReady");
        var state = await harness.Runtime.GetSnapshotAsync();
        var arming = state.ProductionArming!;
        Assert.Equal(ProductionArmAttemptOutcome.ActivatedButNotReady, arming.Outcome);
        Assert.Equal(ProductionArmReason.InputsNotStable, arming.Reason);
        Assert.Equal(attempted.AttemptId, arming.AttemptId);
        Assert.False(state.Ready);
        Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
        Assert.Equal(writesBeforeAttempt, peer.ProductionReadyWriteCount);
        Assert.False(peer.PhysicalProductionReady);
        var armStatus = await WaitForArmStatusWriteAsync(peer, arming.AttemptId);
        Assert.Equal((ushort)ProductionArmAttemptOutcome.ActivatedButNotReady, armStatus.Outcome);
        Assert.Equal((ushort)ProductionArmReason.InputsNotStable, armStatus.Reason);
        Assert.False(armStatus.PhysicalProductionReady);
    }

    [Fact]
    public async Task V147_R07_LocalActivationAndNonProductionWorkScheduleNoAutomaticArm()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer,
            productionArming: new ProductionArmStoreOptions(),
            postActivationArmPolicy: V147AutomaticRearm(),
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)),
            productionArmStatusBinding: V147ArmStatusBinding,
            productionArmMaintenance: new TestProductionArmMaintenanceProvider());

        // A real, graceful non-production Manual Inspection session is local work, not arm evidence.
        AssertAccepted(await harness.StartAsync(), "Manual start");
        var ready = await harness.WaitForSnapshotAsync(
            snapshot => snapshot.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Manual preparation did not become ready");
        AssertAccepted(await harness.ExitAsync(Assert.IsType<Guid>(ready.SessionId),
            ManualInspectionExitMode.Graceful), "Manual graceful exit");

        using var issuer = new ProductionTestIssuer();
        // A local (human) activation and the ordinary human Arm command are not PLC activation.
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness,
            state => state.Ready && state.ArmState == ProductionArmState.Armed,
            "Human arm did not become ready");

        var page = await new SqliteProductionArmHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new ProductionArmHistoryFilter(PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Empty(page.Events);
        Assert.Null((await harness.Runtime.GetSnapshotAsync()).ProductionArming);
    }

    [Fact]
    public async Task V147_R08_InvalidationAtThePhysicalReadyWriteBoundaryNeverConfirmsReady()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)),
            productionArming: new ProductionArmStoreOptions(),
            productionArmStatusBinding: V147ArmStatusBinding,
            postActivationArmPolicy: V147AutomaticRearm(),
            productionArmMaintenance: new TestProductionArmMaintenanceProvider());
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!;
        await EnableRecipeChangeAsync(harness, previous);
        await ArmProductionAsync(harness);
        await WaitConditionAsync(() => peer.PhysicalProductionReady, "Physical Ready before mapped activation");
        var writesBeforeBoundary = peer.ProductionReadyWriteCount;
        var sequence = await RequestRecipeChangeWithFreshBusyAttemptsAsync(harness, peer, 171);
        await WaitRecipeResponseAsync(harness, peer);
        Assert.Equal((ushort)RecipeChangeOutcome.Succeeded, peer.RecipeChangeResponse.Outcome);
        peer.HoldNextProductionReadyWrite();
        await CompleteRecipeChangeAsync(harness, peer, new SqliteRecipeSelectionQuery(harness.Fixture.Options));

        // The rearm is authorized; its physical Ready write is held inside the response so
        // the evidence can change exactly at that boundary.
        await peer.WaitForProductionReadyWriteHeldAsync().WaitAsync(TimeSpan.FromSeconds(20));
        harness.Maintenance!.SetState(ProductionArmMaintenanceState.InProgress);
        peer.ReleaseProductionReadyWrite();

        await WaitProductionAsync(harness, state => state.ProductionArming is
            { Cause: ProductionArmCause.PlcActivation, Outcome: ProductionArmAttemptOutcome.ActivatedButNotReady },
            "Boundary invalidation did not refuse the attempt");
        var state = await harness.Runtime.GetSnapshotAsync();
        var status = Assert.IsType<ProductionArmPolicyStatus>(state.ProductionArming);
        Assert.Equal(ProductionArmReason.AuthorityChanged, status.Reason);
        Assert.False(state.Ready);
        Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
        Assert.Equal(writesBeforeBoundary + 1, peer.ProductionReadyWriteCount);
        await WaitConditionAsync(() => !peer.PhysicalProductionReady,
            "Revocation did not clear the physical Ready");

        var history = await WaitForArmAttemptHistoryAsync(harness, page => page.Events.Any(entry =>
            entry.AttemptId == status.AttemptId && entry.Terminal),
            "Boundary-refused attempt was not closed");
        var terminal = Assert.Single(history.Events, entry => entry.Terminal && entry.AttemptId == status.AttemptId);
        Assert.Equal(ProductionArmEventKind.Failed, terminal.Kind);
        Assert.Equal(ProductionArmReason.AuthorityChanged, terminal.Reason);
        Assert.DoesNotContain(history.Events, entry =>
            entry.AttemptId == status.AttemptId && entry.Kind == ProductionArmEventKind.ReadyConfirmed);
        Assert.Equal(sequence, Assert.Single(history.Events, entry =>
            entry.AttemptId == status.AttemptId && entry.Kind == ProductionArmEventKind.Attempted).PlcRequest!.RequestSequence);

        var armStatus = await WaitForArmStatusWriteAsync(peer, status.AttemptId);
        Assert.True(armStatus.Valid);
        Assert.Equal((ushort)ProductionArmCause.PlcActivation, armStatus.Cause);
        Assert.Equal((ushort)ProductionArmAttemptOutcome.ActivatedButNotReady, armStatus.Outcome);
        Assert.Equal((ushort)ProductionArmReason.AuthorityChanged, armStatus.Reason);
        Assert.False(armStatus.PhysicalProductionReady);
    }

    private static async Task<ProductionArmHistoryPage> WaitForArmAttemptHistoryAsync(ManualHarness harness,
        Func<ProductionArmHistoryPage, bool> predicate, string reason)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        ProductionArmHistoryPage? page = null;
        while (DateTime.UtcNow < deadline)
        {
            page = await new SqliteProductionArmHistoryQuery(harness.Fixture.Options)
                .QueryAsync(new ProductionArmHistoryFilter(PageSize: 128));
            if (page.Available && predicate(page)) return page;
            await Task.Delay(20);
        }
        var events = page?.Events is { } captured
            ? captured.Select(value => value.Kind + ":" + value.ReasonCode) : Array.Empty<string>();
        throw new XunitException(reason + ": " + (page?.ReasonCode ?? "ProductionArmHistoryUnavailable") +
            " events=" + string.Join(";", events));
    }

    private static async Task<ModbusQualificationTestServer.ModbusQualificationProductionArmStatusWrite>
        WaitForArmStatusWriteAsync(ModbusQualificationTestServer peer, Guid attemptId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var wire = peer.ProductionArmStatusWrites.FirstOrDefault(value => value.AttemptId == attemptId);
            if (wire is not null) return wire;
            await Task.Delay(20);
        }
        throw new XunitException(
            "Production arm status was never delivered to the controller for attempt " + attemptId);
    }

    /// <summary>
    /// Development-only maintenance-evidence stand-in. It carries no production authority:
    /// it only lets a test choose the observation the Runtime consumer must judge, and its
    /// station/deployment binding is taken from the exact test deployment.
    /// </summary>
    private sealed class TestProductionArmMaintenanceProvider : IProductionArmMaintenanceEvidenceProvider
    {
        private readonly object _sync = new();
        private ProductionArmMaintenanceEvidence? _current;

        public event Action? Changed;

        public ProductionArmMaintenanceEvidence? Current
        {
            get { lock (_sync) return _current; }
        }

        internal TestProductionArmMaintenanceProvider Attach(string stationId, string deploymentHash,
            string journalHeadHash, ProductionArmMaintenanceState state)
        {
            lock (_sync) _current = new(stationId, deploymentHash, journalHeadHash, state);
            return this;
        }

        internal void SetState(ProductionArmMaintenanceState state)
        {
            lock (_sync)
                _current = _current is null ? null :
                    new(_current.StationId, _current.DeploymentHash, _current.JournalHeadHash, state);
            Changed?.Invoke();
        }
    }
}
