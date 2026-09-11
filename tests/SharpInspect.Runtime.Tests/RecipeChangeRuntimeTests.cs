using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V146_E01_RealMappedPlcActivationCommitsBeforeResponseAndStaysDisarmedAfterReset()
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
        await WaitConditionAsync(() => peer.PhysicalProductionReady, "Physical Ready before mapped activation");

        var sequence = await RequestRecipeChangeWithFreshBusyAttemptsAsync(harness, peer, 101);
        await WaitRecipeResponseAsync(harness, peer);
        var response = peer.RecipeChangeResponse;
        var observedHistory = await new SqliteRecipeSelectionQuery(harness.Fixture.Options)
            .QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
        Assert.True(response.Outcome == (ushort)RecipeChangeOutcome.Succeeded,
            "Mapped activation response: " + response + "; " + string.Join(";",
                observedHistory.Events.Select(value => value.Kind + ":" + value.ReasonCode)));
        Assert.Equal((ushort)RecipeChangeReason.None, response.Reason);
        Assert.Equal(sequence, response.RequestSequence);
        Assert.Equal(7u, response.SelectionCode);
        Assert.False(peer.PhysicalProductionReady);
        var current = await harness.Activations.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        var activated = Assert.IsType<RecipeActivationRecord>(current.Record);
        Assert.NotEqual(previous.Reference, activated.Reference);
        Assert.True(activated.Actor!.IsPlcAdapter);
        Assert.Equal(RecipeActivationEvidenceKind.LocalAuthority, activated.EvidenceKind);
        Assert.Equal(previous.Candidate, activated.Candidate);
        Assert.True(activated.Outcome.Succeeded);
        var state = await harness.Runtime.GetSnapshotAsync();
        Assert.False(state.Ready);
        Assert.Equal(ProductionArmState.Disarmed, state.ArmState);

        var query = new SqliteRecipeSelectionQuery(harness.Fixture.Options);
        var beforeAck = await query.QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
        Assert.True(beforeAck.Available, beforeAck.ReasonCode);
        Assert.Contains(beforeAck.Events, value => value.Kind == RecipeChangeEventKind.DecisionCommitted &&
            value.Activation == activated.Reference);
        Assert.DoesNotContain(beforeAck.Events, value => value.Request.RequestSequence == sequence &&
            value.Kind == RecipeChangeEventKind.AcknowledgementObserved);
        await CompleteRecipeChangeAsync(harness, peer, query);
        var history = await query.QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
        Assert.Equal(new[] { RecipeChangeEventKind.RequestObserved, RecipeChangeEventKind.DecisionCommitted,
            RecipeChangeEventKind.ResponsePublished, RecipeChangeEventKind.AcknowledgementObserved,
            RecipeChangeEventKind.ResponseCleared, RecipeChangeEventKind.ResetObserved }, history.Events
                .Where(value => value.Request.RequestSequence == sequence).Select(value => value.Kind));
        Assert.Equal(ProductionArmState.Disarmed, (await harness.Runtime.GetSnapshotAsync()).ArmState);
        Assert.False(peer.PhysicalProductionReady);
    }

    [Theory]
    [InlineData(false, RecipeChangeReason.LocalOperatorOnly)]
    [InlineData(true, RecipeChangeReason.UnknownCode)]
    public async Task V146_E02_DefaultPolicyAndUnknownCodesReturnDurableRejectionWithoutActivation(
        bool enableMapping, RecipeChangeReason expectedReason)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!;
        if (enableMapping) await EnableRecipeChangeAsync(harness, previous);
        peer.RequestRecipeChange(102, 999);
        await WaitRecipeResponseAsync(harness, peer);
        Assert.Equal((ushort)RecipeChangeOutcome.RejectedUnknownCode, peer.RecipeChangeResponse.Outcome);
        Assert.Equal((ushort)expectedReason, peer.RecipeChangeResponse.Reason);
        Assert.Equal(previous.Reference, (await harness.Activations.ReadCurrentAsync()).Record!.Reference);
        await CompleteRecipeChangeAsync(harness, peer, new(harness.Fixture.Options));
    }

    [Fact]
    public async Task V146_E03_BusyRecipeRequestDoesNotPreemptOrOverwriteAwaitingProductionDelivery()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        peer.AutoAcknowledge = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!;
        await EnableRecipeChangeAsync(harness, previous);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Production ready for conflict");
        peer.RaiseTrigger(61, 1);
        await WaitConditionAsync(() => peer.ResultValidHighCount == 1, "Production result awaiting ACK");
        var first = await WaitForProductionCoreAsync(harness);

        peer.RequestRecipeChange(103, 7);
        await WaitRecipeResponseAsync(harness, peer);
        Assert.Equal((ushort)RecipeChangeOutcome.RejectedBusy, peer.RecipeChangeResponse.Outcome);
        Assert.True(peer.RuntimeResultValid);
        Assert.Equal(previous.Reference, (await harness.Activations.ReadCurrentAsync()).Record!.Reference);
        peer.AcknowledgeRecipeChange();
        await WaitConditionAsync(() => !peer.RecipeChangeResponse.ResponseValid, "Busy recipe response clears");
        peer.ResetRecipeChange();
        Assert.True(peer.RuntimeResultValid);
        peer.AcknowledgeResult();
        peer.SetTrigger(false);
        await WaitConditionAsync(() => peer.AckLowCount == 1, "Existing production delivery completes");
        var history = await WaitForProductionHistoryAsync(harness,
            page => page.Events.Any(value => value.Kind == ProductionInspectionEventKind.AcknowledgementReset),
            "Existing production reset persists");
        Assert.Single(history.Events, value => value.Kind == ProductionInspectionEventKind.CoreCommitted);
        Assert.DoesNotContain(history.Events, value => value.Kind == ProductionInspectionEventKind.FaultTerminated);
        Assert.Contains(history.Events, value => value.Kind == ProductionInspectionEventKind.AcknowledgementReset &&
            value.InspectionId == first.InspectionId);
    }

    [Fact]
    public async Task V146_E04_ReusedIdentityAfterResetFailsClosedWithoutSecondObservationOrResponse()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!.Reference;
        var query = new SqliteRecipeSelectionQuery(harness.Fixture.Options);
        peer.RequestRecipeChange(104, 999);
        await WaitRecipeResponseAsync(harness, peer);
        await CompleteRecipeChangeAsync(harness, peer, query);
        peer.RequestRecipeChange(104, 999);
        await WaitProductionAsync(harness, value => value.Recovery == RecoveryState.Required,
            "Duplicate request closes the owner");
        RecipeChangeHistoryPage history;
        var faultDeadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            history = await query.QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
            Assert.True(history.Available, history.ReasonCode);
            if (history.Events.Any(value => value.Kind == RecipeChangeEventKind.ProtocolFault)) break;
            Assert.True(DateTime.UtcNow < faultDeadline, "Duplicate fault did not persist after recovery publication");
            await Task.Delay(20);
        } while (true);
        Assert.True(history.Available, history.ReasonCode);
        Assert.Single(history.Events, value => value.Kind == RecipeChangeEventKind.RequestObserved);
        Assert.Single(history.Events, value => value.Kind == RecipeChangeEventKind.ResponsePublished);
        Assert.Contains(history.Events, value => value.Kind == RecipeChangeEventKind.ProtocolFault &&
            value.Reason == RecipeChangeReason.DuplicateRequest);
        Assert.False(peer.RecipeChangeResponse.ResponseValid);
        Assert.Equal(previous, (await harness.Activations.ReadCurrentAsync()).Record!.Reference);
    }

    [Fact]
    public async Task V146_E05_CodeChangeDuringPreparationRevokesActivationAndPreservesPreviousRecipe()
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
        await WaitConditionAsync(() => peer.PhysicalProductionReady, "Ready before interrupted preparation");
        harness.Factory.HoldNextCreate();
        uint sequence = 0;
        try
        {
            sequence = await RequestRecipeChangeWithFreshBusyAttemptsAsync(harness, peer, 105,
                harness.Factory.NextCreateEntered);
            try { await harness.Factory.NextCreateEntered.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException)
            {
                var observed = await new SqliteRecipeSelectionQuery(harness.Fixture.Options)
                    .QueryAsync(new RecipeChangeHistoryFilter());
                Assert.Fail("Preparation did not enter: " + peer.RecipeChangeResponse + "; " +
                    string.Join(";", observed.Events.Select(value => value.Kind + ":" + value.ReasonCode)));
            }
            Assert.False(peer.PhysicalProductionReady);
            peer.RequestRecipeChange(sequence, 8);
            var query = new SqliteRecipeSelectionQuery(harness.Fixture.Options);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                var history = await query.QueryAsync(new RecipeChangeHistoryFilter());
                Assert.True(history.Available, history.ReasonCode);
                if (history.Events.Any(value => value.Kind == RecipeChangeEventKind.ProtocolFault &&
                    value.Reason == RecipeChangeReason.RequestChanged)) break;
                Assert.True(DateTime.UtcNow < deadline, "Changed request did not revoke the owner");
                await Task.Delay(20);
            }
        }
        finally { harness.Factory.ReleaseNextCreate(); }
        var terminalDeadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var history = await harness.Activations.QueryAsync(new(PageSize: 20));
            Assert.True(history.Available, history.ReasonCode);
            var terminal = history.Records.SingleOrDefault(value =>
                value.Actor?.PlcRequestContext?.RequestSequence == sequence && value.IsTerminal);
            if (terminal is not null)
            {
                Assert.False(terminal.Outcome.Succeeded);
                Assert.Equal(RecipeActivationRestorationState.NotRequired, terminal.Restoration.State);
                break;
            }
            Assert.True(DateTime.UtcNow < terminalDeadline, "Cancelled activation did not retire");
            await Task.Delay(20);
        }
        Assert.Equal(previous.Reference, (await harness.Activations.ReadCurrentAsync()).Record!.Reference);
        Assert.False(peer.RecipeChangeResponse.ResponseValid);
        Assert.False(peer.PhysicalProductionReady);
        Assert.Equal(ProductionArmState.Disarmed, (await harness.Runtime.GetSnapshotAsync()).ArmState);
    }

    [Fact]
    public async Task V146_E06_FailedCandidateAndFailedRestorationKeepRecoveryRequiredAndPreviousAuditReference()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)),
            cameraConfigurationPlans: new[]
            {
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero),
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.WriteFailure, TimeSpan.Zero),
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.WriteFailure, TimeSpan.Zero)
            });
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!;
        await EnableRecipeChangeAsync(harness, previous);
        peer.RequestRecipeChange(106, 7);
        // Recovery is expected here; wait for the immutable failure rather than
        // using the ordinary success-response helper's healthy-owner precondition.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        RecipeActivationRecord? terminal;
        do
        {
            var history = await harness.Activations.QueryAsync(new(PageSize: 20));
            Assert.True(history.Available, history.ReasonCode);
            terminal = history.Records.SingleOrDefault(value => value.Actor?.IsPlcAdapter == true && value.IsTerminal);
            if (terminal is not null) break;
            Assert.True(DateTime.UtcNow < deadline, "Failed restoration did not persist terminal evidence");
            await Task.Delay(20);
        } while (true);
        Assert.False(terminal.Outcome.Succeeded);
        Assert.Equal(RecipeActivationRestorationState.Failed, terminal.Restoration.State);
        Assert.Equal(previous.Reference, terminal.PreviousActivation);
        await WaitProductionAsync(harness, value => value.Recovery == RecoveryState.Required,
            "Failed restoration requires recovery");
        Assert.False(peer.PhysicalProductionReady);
        Assert.Equal(ProductionArmState.Disarmed, (await harness.Runtime.GetSnapshotAsync()).ArmState);
        Assert.Equal(previous.Reference, (await harness.Activations.ReadCurrentAsync()).Record!.Reference);
        Assert.NotEqual((ushort)RecipeChangeOutcome.Succeeded, peer.RecipeChangeResponse.Outcome);
        var query = new SqliteRecipeSelectionQuery(harness.Fixture.Options);
        var decisionDeadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var history = await query.QueryAsync(new RecipeChangeHistoryFilter());
            Assert.True(history.Available, history.ReasonCode);
            var decision = history.Events.SingleOrDefault(value => value.Kind == RecipeChangeEventKind.DecisionCommitted);
            if (decision is not null)
            {
                Assert.Equal(RecipeChangeReason.RestorationFailed, decision.Reason);
                Assert.Equal(terminal.Reference, decision.Activation);
                break;
            }
            Assert.True(DateTime.UtcNow < decisionDeadline, "Failed restoration has no durable recipe decision");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task V146_E08_ExactRetirementVetoFixtureRefusesMappedReleaseBeforeActivation()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var previous = (await harness.Activations.ReadCurrentAsync()).Record!;
        await EnableRecipeChangeAsync(harness, previous);
        var target = new RecipeSelectionMapEntry(7, previous.Candidate, previous.ReleaseId, previous.ReleaseRecordContentHash);
        var veto = new PlcRecipeRetirementVeto(new[] { target });
        Assert.False(veto.IsRetired(new(8, new RecipeReference(previous.Candidate.Id, "different-version", previous.Candidate.ContentHash),
            previous.ReleaseId, previous.ReleaseRecordContentHash)));
        Assert.IsType<StationRuntime>(harness.Runtime).ConfigurePlcRecipeRetirementFixture(veto);
        var prepared = harness.Factory.Created;
        var cameraOpens = harness.CameraProvider.GetDiagnostics().Devices.Sum(value => value.OpenCount);
        peer.RequestRecipeChange(108, 7);
        await WaitRecipeResponseAsync(harness, peer);
        Assert.Equal((ushort)RecipeChangeOutcome.FailedActivation, peer.RecipeChangeResponse.Outcome);
        Assert.Equal((ushort)RecipeChangeReason.RecipeRetired, peer.RecipeChangeResponse.Reason);
        Assert.Equal(prepared, harness.Factory.Created);
        Assert.Equal(cameraOpens, harness.CameraProvider.GetDiagnostics().Devices.Sum(value => value.OpenCount));
        Assert.Equal(previous.Reference, (await harness.Activations.ReadCurrentAsync()).Record!.Reference);
        var query = new SqliteRecipeSelectionQuery(harness.Fixture.Options);
        await CompleteRecipeChangeAsync(harness, peer, query);
        var history = await query.QueryAsync(new RecipeChangeHistoryFilter());
        Assert.True(history.Available, history.ReasonCode);
        Assert.All(history.Events, value => Assert.Null(value.Activation));
        Assert.All(history.Events, value => Assert.Equal(target.ContentHash, value.Request.Target!.ContentHash));
    }

    private static async Task EnableRecipeChangeAsync(ManualHarness harness, RecipeActivationRecord target)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            // Snapshot projection and the identity writer deliberately decline lock contention.
            // A rejected setup command changed no Map; a fresh operator attempt needs its own grant.
            var command = new ChangeRecipeSelectionCommand(Guid.NewGuid(), harness.Invocation(),
                new("V146.Isolated.Selection", "1", RecipeSelectionMode.PlcRequestedActivation),
                new("V146.Isolated.Map", "1", new[] { new RecipeSelectionMapEntry(7, target.Candidate,
                    target.ReleaseId, target.ReleaseRecordContentHash) }), null, "V146 map one exact isolated release");
            var grant = await ProductionGrantAsync(harness, Permission.ManageRecipeSelectionMap,
                command.CorrelationId, command.AuthorizationTarget, AuditedCommandKind.ChangeRecipeSelection);
            var result = await harness.Service<IRecipeSelectionService>().ChangeAsync(command with
                { Invocation = command.Invocation with { StepUpGrantId = grant } });
            if (result.Outcome.Disposition == CommandDisposition.Rejected &&
                result.Outcome.ReasonCode == "RecipeSelectionRuntimeBusy" && DateTime.UtcNow < deadline)
            {
                Assert.Null(result.Revision);
                await Task.Delay(20);
                continue;
            }
            AssertAccepted(result.Outcome, "Mapped selection governance");
            return;
        }
    }

    private static async Task WaitRecipeResponseAsync(ManualHarness harness, ModbusQualificationTestServer peer)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!peer.RecipeChangeResponse.ResponseValid)
        {
            if (DateTime.UtcNow >= deadline)
            {
                var state = await harness.Runtime.GetSnapshotAsync();
                Assert.Fail("Recipe response unavailable: " + state.LastCommand + "; " + state.PlcCommunication?.ReasonCode);
            }
            // Observe the wire here. Continuously projecting a Runtime snapshot also takes
            // the non-waiting activation gate's lock and creates an unrelated busy outcome.
            await Task.Delay(20);
        }
    }

    private static async Task CompleteRecipeChangeAsync(ManualHarness harness, ModbusQualificationTestServer peer,
        SqliteRecipeSelectionQuery query)
    {
        var sequence = peer.RecipeChangeResponse.RequestSequence;
        peer.AcknowledgeRecipeChange();
        await WaitConditionAsync(() => !peer.RecipeChangeResponse.ResponseValid, "Recipe response clear after real ACK");
        peer.ResetRecipeChange();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var history = await query.QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
            Assert.True(history.Available, history.ReasonCode);
            if (history.Events.Any(value => value.Request.RequestSequence == sequence &&
                value.Kind == RecipeChangeEventKind.ResetObserved)) break;
            Assert.True(DateTime.UtcNow < deadline, "Recipe reset observation unavailable");
            await Task.Delay(20);
        }
        Assert.False((await harness.Runtime.GetSnapshotAsync()).Busy);
    }
}
