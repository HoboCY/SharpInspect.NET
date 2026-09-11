using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// T47 Runtime integration coverage for the maintenance-gated production arm paths.
/// Every case composes the real StationRuntime, the real schema-32 arm ledger, the real
/// qualification issuer (ProductionTestIssuer) and the controller-owned Modbus peer. The
/// fixture maintenance-evidence provider is an explicitly development-only stand-in bound
/// to the exact test station and deployment content hash: it exercises the consumer's
/// head/state judgement, never qualifies a deployment and never stands in for a production
/// maintenance journal reader.
/// </summary>
public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V147_R10_ManualArmRequiredNeedsTheAuthenticatedHumanCommandAndItsExactHead()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)),
            productionArming: new ProductionArmStoreOptions(),
            productionArmStatusBinding: V147ArmStatusBinding,
            postActivationArmPolicy: V147AutomaticRearm(),
            productionArmMaintenance: new TestProductionArmMaintenanceProvider(),
            productionArmMaintenanceState: ProductionArmMaintenanceState.ManualArmRequired);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await harness.Fixture.WaitForVerifiedAsync();
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!;
        await EnableRecipeChangeAsync(harness, previous);
        var head = harness.Maintenance!.Current!.JournalHeadHash;

        // The deployment default is Manual Arm and no PLC handshake has happened, so the
        // required maintenance head can never be pre-fulfilled by the start-up path.
        var startup = await new SqliteProductionArmHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new ProductionArmHistoryFilter(PageSize: 128));
        Assert.True(startup.Available, startup.ReasonCode);
        Assert.DoesNotContain(startup.Events, entry => entry.Kind == ProductionArmEventKind.ReadyConfirmed);
        Assert.DoesNotContain(startup.Events, entry => entry.Cause == ProductionArmCause.ManualMaintenanceArm);
        Assert.Equal(ProductionArmState.Disarmed, (await harness.Runtime.GetSnapshotAsync()).ArmState);

        // Freeze the first physical Runtime-ready write inside its response. The required
        // head may not be marked fulfilled before the controller acknowledges that write.
        peer.HoldNextProductionReadyWrite();
        var (arm, _) = await SubmitManualMaintenanceArmAsync(harness);
        await peer.WaitForProductionReadyWriteHeldAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var heldStateTask = harness.Runtime.GetSnapshotAsync().AsTask();
        var heldHistoryTask = new SqliteProductionArmHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new ProductionArmHistoryFilter(PageSize: 128)).AsTask();
        await Task.WhenAll(heldStateTask, heldHistoryTask);
        StationStateSnapshot heldState;
        ProductionArmHistoryPage heldHistory;
        try
        {
            heldState = await heldStateTask;
            heldHistory = await heldHistoryTask;
        }
        finally
        {
            peer.ReleaseProductionReadyWrite();
        }
        Assert.True(heldHistory.Available, heldHistory.ReasonCode);
        Assert.Equal(ProductionArmState.Armed, heldState.ArmState);
        Assert.False(heldState.Ready);
        var heldAttempted = Assert.Single(heldHistory.Events, entry =>
            entry.Kind == ProductionArmEventKind.Attempted);
        Assert.Equal(ProductionArmCause.ManualMaintenanceArm, heldAttempted.Cause);
        Assert.Equal(head, heldAttempted.MaintenanceHeadHash);
        Assert.Equal(arm.CorrelationId, heldAttempted.HumanCommandId);
        Assert.DoesNotContain(heldHistory.Events, entry => entry.Kind == ProductionArmEventKind.ReadyConfirmed);
        Assert.DoesNotContain(heldHistory.Events, entry => entry.Terminal);

        await WaitProductionAsync(harness, state => state.Ready && state.ArmState == ProductionArmState.Armed,
            "Manual maintenance arm did not reach physical Ready");
        var history = await WaitForArmAttemptHistoryAsync(harness, page => page.Events.Any(entry =>
            entry.Cause == ProductionArmCause.ManualMaintenanceArm &&
            entry.Kind == ProductionArmEventKind.ReadyConfirmed),
            "Manual maintenance ReadyConfirmed was not recorded");
        history = await WaitForArmAttemptHistoryAsync(harness, page => page.Events.Any(entry =>
            entry.Cause == ProductionArmCause.ManualMaintenanceArm && entry.StatusDelivery),
            "Manual maintenance status disposition was not recorded");
        var manual = history.Events.Where(entry => entry.Cause == ProductionArmCause.ManualMaintenanceArm).ToArray();
        // The writer appends one explicit status disposition after the terminal record, so
        // the state-bearing end is selected by kind rather than by "last event".
        Assert.Equal(new[] { ProductionArmEventKind.Attempted, ProductionArmEventKind.Authorized,
            ProductionArmEventKind.ReadyConfirmed, ProductionArmEventKind.PlcStatusUndelivered },
            manual.Select(entry => entry.Kind));
        var attempted = manual[0];
        var authorized = manual[1];
        var confirmed = manual[2];
        var notPublished = manual[3];
        Assert.Equal(ProductionArmReason.ConfigurationUnavailable, notPublished.Reason);
        Assert.Equal(ProductionArmReason.None, confirmed.Reason);
        Assert.Equal(attempted.AttemptId, confirmed.AttemptId);
        Assert.Equal(harness.Deployment!.ContentHash, confirmed.DeploymentHash);
        Assert.Equal(harness.Fixture.Options.LocalIdentity!.StationId, confirmed.StationId);
        Assert.Equal(head, confirmed.MaintenanceHeadHash);
        Assert.NotNull(confirmed.Report);
        Assert.True(confirmed.Report!.CanArm);
        Assert.True(ProductionArmHeads.Equal(confirmed.ExpectedDurableHeads, confirmed.CurrentDurableHeads));
        Assert.Null(attempted.PlcRequest);
        Assert.Null(confirmed.PlcRequest);

        // The manual cause is attributed to the actual authenticated human, never to the
        // Runtime actor, and the exact command identity is retained by every record.
        var invocation = harness.Invocation();
        var humanPrincipal = Guid.Parse(Assert.IsType<string>(invocation.PrincipalId));
        var humanSession = Assert.IsType<Guid>(invocation.SessionId);
        Assert.All(manual, entry =>
        {
            Assert.Equal(arm.CorrelationId, entry.HumanCommandId);
            Assert.Equal(humanPrincipal, entry.HumanPrincipalId);
            Assert.Equal(humanSession, entry.HumanSessionId);
            Assert.Equal(humanPrincipal.ToString("D"), entry.ActorPrincipalId);
            Assert.Equal(humanSession, entry.ActorSessionId);
            Assert.False(entry.RuntimeActor);
        });

        // The signed receipt is the first physical acknowledgement: it binds the exact
        // authorization event, activation, maintenance head and controller generation.
        var receipt = Assert.IsType<ProductionArmReadyReceipt>(confirmed.ReadyReceipt);
        Assert.Equal(confirmed.AttemptId, receipt.AttemptId);
        Assert.Equal(confirmed.RuntimeEpoch, receipt.RuntimeEpoch);
        Assert.Equal(authorized.ContentHash, receipt.AuthorizationEventHash);
        Assert.Equal(head, receipt.MaintenanceHeadHash);
        Assert.NotNull(receipt.Activation);
        Assert.Equal(confirmed.AdmissionGeneration, receipt.AdmissionGeneration);
        Assert.NotEqual(0u, receipt.ControllerEpoch);
        Assert.True(receipt.ObservedAtUtc >= authorized.RecordedAtUtc);

        Assert.True(peer.PhysicalProductionReady);
        Assert.True(peer.ProductionReadyWriteCount >= 1);
        // The manual confirmation is not echoed as a controller status block.
        Assert.DoesNotContain(peer.ProductionArmStatusWrites, value => value.AttemptId == confirmed.AttemptId);

        // The mapped PLC recipe change reserves the disarm through its own reservation; the
        // post-activation automatic rearm may be permitted only by this exact verified head.
        var sequence = await RequestRecipeChangeWithFreshBusyAttemptsAsync(harness, peer, 211);
        await WaitRecipeResponseAsync(harness, peer);
        Assert.Equal((ushort)RecipeChangeOutcome.Succeeded, peer.RecipeChangeResponse.Outcome);
        await CompleteRecipeChangeAsync(harness, peer, new SqliteRecipeSelectionQuery(harness.Fixture.Options));

        await WaitProductionAsync(harness, state => state.Ready && state.ProductionArming is
            { Cause: ProductionArmCause.PlcActivation, Outcome: ProductionArmAttemptOutcome.ReadyConfirmed,
                PlcStatusDelivered: true },
            "Post-activation automatic rearm did not confirm physical Ready under the same manual head");
        var settled = await harness.Runtime.GetSnapshotAsync();
        var autoStatus = Assert.IsType<ProductionArmPolicyStatus>(settled.ProductionArming);
        Assert.Equal(sequence, autoStatus.RequestSequence);
        Assert.Equal(7u, autoStatus.SelectionCode);
        var autoHistory = await WaitForArmAttemptHistoryAsync(harness, page => page.Events.Any(entry =>
            entry.AttemptId == autoStatus.AttemptId && entry.Kind == ProductionArmEventKind.PlcStatusDelivered),
            "Automatic rearm has no delivered status observation");
        var automatic = autoHistory.Events.Where(entry => entry.AttemptId == autoStatus.AttemptId).ToArray();
        Assert.Equal(new[] { ProductionArmEventKind.Attempted, ProductionArmEventKind.Authorized,
            ProductionArmEventKind.ReadyConfirmed, ProductionArmEventKind.PlcStatusDelivered },
            automatic.Select(entry => entry.Kind));
        var automaticAttempted = automatic[0];
        var automaticAuthorized = automatic[1];
        var automaticConfirmed = automatic[2];
        Assert.Equal(sequence, automaticAttempted.PlcRequest!.RequestSequence);
        Assert.Equal(head, automaticAttempted.MaintenanceHeadHash);
        Assert.Equal(head, automaticAuthorized.MaintenanceHeadHash);
        Assert.Equal(confirmed.MaintenanceHeadHash, automaticConfirmed.MaintenanceHeadHash);
        Assert.NotNull(automaticConfirmed.ReadyReceipt);
        // The system path is never attributed to a human identity.
        Assert.True(automaticConfirmed.RuntimeActor);
        Assert.Null(automaticConfirmed.HumanCommandId);
        Assert.Equal(SystemPrincipalId.Runtime, automaticConfirmed.ActorPrincipalId);
        Assert.Null(automaticConfirmed.ActorSessionId);
        // The same-head automatic authorization exists only after the manual confirmation.
        Assert.True(confirmed.Position < automaticAuthorized.Position);
    }

    [Fact]
    public async Task V147_R11_InProgressMaintenanceDeniesTheHumanArmWhileEveryAdmissionGatePasses()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer,
            productionArming: new ProductionArmStoreOptions(),
            productionArmMaintenance: new TestProductionArmMaintenanceProvider(),
            productionArmMaintenanceState: ProductionArmMaintenanceState.InProgress);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await harness.Fixture.WaitForVerifiedAsync();

        var (arm, outcome) = await SubmitManualMaintenanceArmAsync(harness);

        // The final publish for this exact command proves the attempt closed without arming.
        await WaitProductionAsync(harness, state => state.LastCommand is { State: OperationState.Failed } progress &&
            progress.CorrelationId == arm.CorrelationId,
            "In-progress maintenance arm did not close disarmed");
        var state = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
        Assert.False(state.Ready);
        // Only the maintenance observation refuses it: the real admission gates still pass.
        Assert.True(state.ProductionAdmission is { CanArm: true });
        Assert.True(outcome.ProductionAdmission is { CanArm: true });
        Assert.False(peer.PhysicalProductionReady);
        Assert.Equal(0, peer.ProductionReadyWriteCount);
        var page = await new SqliteProductionArmHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new ProductionArmHistoryFilter(PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        Assert.DoesNotContain(page.Events, entry => entry.Kind == ProductionArmEventKind.ReadyConfirmed);
        Assert.DoesNotContain(page.Events, entry => entry.Cause == ProductionArmCause.ManualMaintenanceArm);
        Assert.DoesNotContain(page.Events, entry => entry.Terminal);
    }

    [Fact]
    public async Task V147_R12_ChangedMaintenanceHeadIsNotSatisfiedByTheEarlierManualConfirmation()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)),
            productionArming: new ProductionArmStoreOptions(),
            productionArmStatusBinding: V147ArmStatusBinding,
            postActivationArmPolicy: V147AutomaticRearm(),
            productionArmMaintenance: new TestProductionArmMaintenanceProvider(),
            productionArmMaintenanceState: ProductionArmMaintenanceState.ManualArmRequired);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await harness.Fixture.WaitForVerifiedAsync();
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!;
        await EnableRecipeChangeAsync(harness, previous);
        var oldHead = harness.Maintenance!.Current!.JournalHeadHash;

        await SubmitManualMaintenanceArmAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready && state.ArmState == ProductionArmState.Armed,
            "First manual maintenance arm did not reach physical Ready");
        var first = await WaitForArmAttemptHistoryAsync(harness, page => page.Events.Any(entry =>
            entry.Cause == ProductionArmCause.ManualMaintenanceArm &&
            entry.Kind == ProductionArmEventKind.ReadyConfirmed),
            "First manual confirmation was not recorded");
        Assert.Equal(oldHead, Assert.Single(first.Events, entry =>
            entry.Cause == ProductionArmCause.ManualMaintenanceArm &&
            entry.Kind == ProductionArmEventKind.ReadyConfirmed).MaintenanceHeadHash);

        // The maintenance journal advances to a new head after the first confirmation. The
        // provider keeps its station/deployment binding and only the head changes, so the
        // earlier head's confirmation can never be re-read as evidence for the new head.
        var newHead = new string('C', 64);
        harness.Maintenance!.Attach(harness.Fixture.Options.LocalIdentity!.StationId,
            harness.Deployment!.ContentHash, newHead, ProductionArmMaintenanceState.ManualArmRequired);
        Assert.Equal(newHead, harness.Maintenance.Current!.JournalHeadHash);
        Assert.NotEqual(oldHead, newHead);

        var sequence = await RequestRecipeChangeWithFreshBusyAttemptsAsync(harness, peer, 221);
        await WaitRecipeResponseAsync(harness, peer);
        Assert.Equal((ushort)RecipeChangeOutcome.Succeeded, peer.RecipeChangeResponse.Outcome);
        await CompleteRecipeChangeAsync(harness, peer, new SqliteRecipeSelectionQuery(harness.Fixture.Options));

        var history = await WaitForArmAttemptHistoryAsync(harness, page => page.Events.Any(entry =>
            entry.Cause == ProductionArmCause.PlcActivation && entry.Terminal),
            "Changed-head post-activation attempt was not recorded");
        var attempted = Assert.Single(history.Events, entry =>
            entry.Cause == ProductionArmCause.PlcActivation && entry.Kind == ProductionArmEventKind.Attempted);
        Assert.Equal(newHead, attempted.MaintenanceHeadHash);
        Assert.Equal(sequence, attempted.PlcRequest!.RequestSequence);
        var terminal = Assert.Single(history.Events, entry => entry.AttemptId == attempted.AttemptId && entry.Terminal);
        Assert.Equal(ProductionArmEventKind.Rejected, terminal.Kind);
        Assert.Equal(ProductionArmReason.ManualArmAfterMaintenanceRequired, terminal.Reason);
        Assert.Equal(newHead, terminal.MaintenanceHeadHash);
        Assert.DoesNotContain(history.Events, entry =>
            entry.AttemptId == attempted.AttemptId && entry.Kind == ProductionArmEventKind.Authorized);
        Assert.DoesNotContain(history.Events, entry =>
            entry.AttemptId == attempted.AttemptId && entry.Kind == ProductionArmEventKind.ReadyConfirmed);
        Assert.DoesNotContain(history.Events, entry =>
            entry.Cause == ProductionArmCause.PlcActivation && entry.MaintenanceHeadHash == oldHead);
        // The earlier head's confirmation is untouched: the new head was refused on its own.
        Assert.Equal(oldHead, Assert.Single(history.Events, entry =>
            entry.Cause == ProductionArmCause.ManualMaintenanceArm &&
            entry.Kind == ProductionArmEventKind.ReadyConfirmed).MaintenanceHeadHash);

        await WaitProductionAsync(harness, state => state.ProductionArming is
            { Cause: ProductionArmCause.PlcActivation, Outcome: ProductionArmAttemptOutcome.ActivatedButNotReady,
                Reason: ProductionArmReason.ManualArmAfterMaintenanceRequired },
            "Changed-head post-activation attempt did not close ActivatedButNotReady");
        var state = await harness.Runtime.GetSnapshotAsync();
        var status = Assert.IsType<ProductionArmPolicyStatus>(state.ProductionArming);
        Assert.Equal(attempted.AttemptId, status.AttemptId);
        Assert.False(state.Ready);
        Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
        Assert.False(peer.PhysicalProductionReady);
        var wire = await WaitForArmStatusWriteAsync(peer, status.AttemptId);
        Assert.True(wire.Valid);
        Assert.Equal((ushort)ProductionArmCause.PlcActivation, wire.Cause);
        Assert.Equal((ushort)ProductionArmAttemptOutcome.ActivatedButNotReady, wire.Outcome);
        Assert.Equal((ushort)ProductionArmReason.ManualArmAfterMaintenanceRequired, wire.Reason);
        Assert.Equal(sequence, wire.RequestSequence);
        Assert.False(wire.PhysicalProductionReady);
    }

    /// <summary>
    /// Mirrors the ordinary human Arm helper while retaining the exact command identity that
    /// the manual-maintenance ledger attribution is compared against. The recovered start-up
    /// alarms still require the real authorization path before the real admission can pass.
    /// </summary>
    private static async Task<(ArmProductionCommand Command, RuntimeCommandOutcome Outcome)>
        SubmitManualMaintenanceArmAsync(ManualHarness harness)
    {
        var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
        await WaitProductionAsync(harness, state => state.AlarmState is { Available: true }, "Alarm authority");
        var state = await runtime.GetSnapshotAsync();
        foreach (var alarm in state.AlarmState!.Instances.Where(alarm => alarm.Lifecycle == AlarmLifecycle.RecoveredLatched))
        {
            AssertAccepted(await runtime.SubmitAsync(new AcknowledgeAlarmCommand(Guid.NewGuid(), harness.Invocation(), alarm.InstanceId)),
                "Acknowledge recovered startup alarm");
            var correlation = Guid.NewGuid();
            var grant = await ProductionGrantAsync(harness, Permission.ResetAlarm, correlation,
                alarm.InstanceId.ToString("D"), AuditedCommandKind.ResetAlarm);
            AssertAccepted(await runtime.SubmitAsync(new ResetAlarmCommand(correlation,
                harness.Invocation() with { StepUpGrantId = grant }, alarm.InstanceId)), "Reset recovered startup alarm");
        }
        await runtime.RefreshProductionAdmissionAsync();
        await WaitProductionAsync(harness, value => value.ProductionAdmission?.CanArm == true,
            "Real production admission");
        var command = new ArmProductionCommand(Guid.NewGuid(), harness.Invocation());
        var outcome = await runtime.SubmitAsync(command);
        Assert.Equal(command.CorrelationId, outcome.CorrelationId);
        Assert.True(outcome.Audit == AuditPersistence.Persisted,
            "Production maintenance Arm: " + outcome.Disposition + "/" + outcome.ReasonCode +
            "; audit=" + outcome.Audit);
        return (command, outcome);
    }
}
