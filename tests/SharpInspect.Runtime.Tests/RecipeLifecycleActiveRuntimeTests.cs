using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData("Idle")]
    [InlineData("Execution")]
    [InlineData("AckHigh")]
    [InlineData("AckLow")]
    public async Task V148_R03_ActiveRetirementCommitsOnlyAfterAcceptedProductionActuallyRetires(string stage)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        peer.AutoAcknowledge = stage is "Idle" or "AckLow";
        peer.AutoClearAcknowledge = stage != "AckLow";
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            enableRecipeLifecycle: true);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var active = (await harness.Activations.ReadCurrentAsync()).Record!;
        var command = await RetirementCommandAsync(harness, active);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Ready for lifecycle transaction");
        var lifecycle = harness.Service<IRecipeLifecycleService>();
        if (stage == "Execution") harness.Factory.HoldExecution();
        try
        {
            if (stage != "Idle")
            {
                peer.RaiseTrigger(61, 1);
                if (stage == "Execution") await harness.Factory.ExecutionEntered.WaitAsync(TimeSpan.FromSeconds(10));
                else await WaitProductionAsync(harness, state => state.Handshake ==
                    (stage == "AckHigh" ? HandshakePhase.AwaitingResultAck : HandshakePhase.AwaitingAckReset), "Retained ACK");
            }
            var retirement = lifecycle.RetireAsync(command).AsTask();
            if (stage != "Idle")
            {
                await WaitProductionAsync(harness, state => state.AdmissionBlockers.Contains("RecipeRetirementInProgress"), "Retirement draining");
                Assert.False(retirement.IsCompleted);
                Assert.Equal(CommandDisposition.Rejected, (await harness.Runtime.SubmitAsync(
                    new ArmProductionCommand(Guid.NewGuid(), harness.Invocation()))).Disposition);
                Assert.Empty((await lifecycle.QueryAsync(new())).Records);
                Assert.Equal(active.Reference, (await harness.Activations.ReadCurrentAsync()).Record?.Reference);
                harness.Factory.ReleaseExecution();
                if (stage != "AckLow")
                {
                    await peer.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    peer.AcknowledgeResult();
                }
                else { peer.AutoClearAcknowledge = true; peer.ResetResultAcknowledgement(); }
                peer.SetTrigger(false);
            }
            var result = await retirement.WaitAsync(TimeSpan.FromSeconds(20));
            AssertAccepted(result.Outcome, "Atomic active retirement");
            var record = Assert.IsType<RecipeLifecycleRecord>(result.Record);
            Assert.Equal(active.Reference, record.ClearedActive);
            Assert.False(result.RuntimeRecoveryRequired, result.CleanupReasonCode);
            var snapshot = await harness.Runtime.GetSnapshotAsync();
            Assert.Null(snapshot.ActiveRecipe);
            Assert.False(snapshot.Ready);
            Assert.Equal(ProductionArmState.Disarmed, snapshot.ArmState);
            var cold = await new SqliteRecipeActivationQuery(harness.Fixture.Options).ReadCurrentAsync();
            Assert.True(cold.Available, cold.ReasonCode);
            Assert.Null(cold.Record);
            Assert.Equal(ReleasedRecipeLifecycleState.Retired, (await lifecycle.ReadReleaseAsync(active.Candidate,
                active.ReleaseId, active.ReleaseRecordContentHash)).State);
            if (stage != "Idle")
            {
                var cycles = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).QueryAsync(new(PageSize: 128));
                Assert.True(cycles.Available, cycles.ReasonCode);
                Assert.Single(cycles.Events, value => value.Kind == ProductionInspectionEventKind.CoreCommitted);
                Assert.Contains(cycles.Events, value => value.Kind == ProductionInspectionEventKind.AcknowledgementReset);
                Assert.DoesNotContain(cycles.Events, value => value.Kind == ProductionInspectionEventKind.FaultTerminated);
            }
        }
        finally { ReleaseLifecycleCycle(harness, peer); }
    }

    [Fact]
    public async Task V148_R04_CommittedRetirementRetainsRecoveryWhileActualPluginDisposalIsBlocked()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            enableRecipeLifecycle: true);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var active = (await harness.Activations.ReadCurrentAsync()).Record!;
        var command = await RetirementCommandAsync(harness, active);
        harness.Factory.HoldNextDispose();
        try
        {
            var retirement = harness.Service<IRecipeLifecycleService>().RetireAsync(command).AsTask();
            await harness.Factory.DisposeEntered.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await retirement.WaitAsync(TimeSpan.FromSeconds(10));
            AssertAccepted(result.Outcome, "Commit survives cleanup timeout");
            Assert.True(result.RuntimeRecoveryRequired);
            Assert.Equal("RecipeRetirementCleanupIncomplete", result.CleanupReasonCode);
            var state = await harness.Runtime.GetSnapshotAsync();
            Assert.Null(state.ActiveRecipe);
            Assert.False(state.Ready);
            Assert.Equal(RecoveryState.Required, state.Recovery);
            Assert.Null((await new SqliteRecipeActivationQuery(harness.Fixture.Options).ReadCurrentAsync()).Record);
            Assert.Single((await harness.Service<IRecipeLifecycleService>().QueryAsync(new())).Records);
        }
        finally { harness.Factory.ReleaseDispose(); }
        await harness.Factory.AlgorithmDisposeCompleted.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task V148_R05_CancellationDuringRetainedAckCannotCreateRetirementOrRearm()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        peer.AutoAcknowledge = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            enableRecipeLifecycle: true);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var active = (await harness.Activations.ReadCurrentAsync()).Record!;
        var command = await RetirementCommandAsync(harness, active);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Ready before cancelled retirement");
        using var cancel = new CancellationTokenSource();
        try
        {
            peer.RaiseTrigger(61, 1);
            await WaitProductionAsync(harness, state => state.Handshake == HandshakePhase.AwaitingResultAck, "Retained ACK before cancel");
            var retirement = harness.Service<IRecipeLifecycleService>().RetireAsync(command, cancel.Token).AsTask();
            await WaitProductionAsync(harness, state => state.AdmissionBlockers.Contains("RecipeRetirementInProgress"), "Retirement started");
            cancel.Cancel();
            var result = await retirement.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(CommandDisposition.Rejected, result.Outcome.Disposition);
            Assert.Null(result.Record);
            Assert.Empty((await harness.Service<IRecipeLifecycleService>().QueryAsync(new())).Records);
            Assert.Equal(active.Reference, (await harness.Activations.ReadCurrentAsync()).Record?.Reference);
            var state = await harness.Runtime.GetSnapshotAsync();
            Assert.False(state.Ready);
            Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
            Assert.True(peer.RuntimeResultValid);
        }
        finally { ReleaseLifecycleCycle(harness, peer); }
        await WaitProductionAsync(harness, state => state.Handshake == HandshakePhase.Idle, "Accepted cycle completes after cancellation");
    }

    [Fact]
    public async Task V148_R06_FailedLifecycleInsertRollsBackFactAndPreservesOldActiveDisarmed()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            enableRecipeLifecycle: true);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var active = (await harness.Activations.ReadCurrentAsync()).Record!;
        var command = await RetirementCommandAsync(harness, active);
        using var connection = new SqliteConnection("Data Source=" + harness.Fixture.Options.DatabasePath + ";Pooling=False");
        connection.Open();
        void Execute(string sql) { using var statement = connection.CreateCommand(); statement.CommandText = sql; statement.ExecuteNonQuery(); }
        Execute("CREATE TRIGGER v148_fail_lifecycle BEFORE INSERT ON recipe_lifecycle_events BEGIN SELECT RAISE(ABORT,'V148InjectedLifecycleWriteFailure'); END;");
        try
        {
            var result = await harness.Service<IRecipeLifecycleService>().RetireAsync(command);
            Assert.Equal(CommandDisposition.Rejected, result.Outcome.Disposition);
            Assert.Equal(AuditPersistence.Unavailable, result.Outcome.Audit);
            Assert.Null(result.Record);
            Assert.Empty((await harness.Service<IRecipeLifecycleService>().QueryAsync(new())).Records);
            Assert.Equal(active.Reference, (await harness.Activations.ReadCurrentAsync()).Record?.Reference);
            Assert.Equal(active.Candidate, (await harness.Runtime.GetSnapshotAsync()).ActiveRecipe);
            Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
        }
        finally { Execute("DROP TRIGGER v148_fail_lifecycle;"); }
    }

    private static async Task<RetireReleasedRecipeCommand> RetirementCommandAsync(ManualHarness harness, RecipeActivationRecord active)
    {
        var command = new RetireReleasedRecipeCommand(Guid.NewGuid(), harness.Invocation(), active.Candidate,
            active.ReleaseId, active.ReleaseRecordContentHash, active.Reference, "V148 retire exact active production version");
        var grant = await ProductionGrantAsync(harness, Permission.RetireRecipe, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.RetireReleasedRecipe);
        return command with { Invocation = command.Invocation with { StepUpGrantId = grant } };
    }

    [Fact]
    public async Task V148_R07_RetiredCurrentHasNoAutomaticFallbackAndOlderAvailableRequiresFreshOrdinaryValidation()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            enableRecipeLifecycle: true);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var older = (await harness.Activations.ReadCurrentAsync()).Record!;
        var newer = await ReleaseLifecycleCandidateAsync(harness);
        var activated = await ActivateLifecycleCandidateAsync(harness, newer.Reference, newer.Record.ReleaseId,
            newer.Record.ContentHash, older.Reference);
        AssertAccepted(activated.Outcome, "Activate newer version");
        var active = Assert.IsType<RecipeActivationRecord>(activated.Record);
        var retired = await harness.Service<IRecipeLifecycleService>().RetireAsync(await RetirementCommandAsync(harness, active));
        AssertAccepted(retired.Outcome, "Retire newest version");
        var query = new SqliteRecipeActivationQuery(harness.Fixture.Options);
        var empty = await query.ReadCurrentAsync();
        Assert.True(empty.Available, empty.ReasonCode);
        Assert.Null(empty.Record);
        Assert.Null((await harness.Runtime.GetSnapshotAsync()).ActiveRecipe);
        Assert.Equal(ReleasedRecipeLifecycleState.Available, (await harness.Service<IRecipeLifecycleService>()
            .ReadReleaseAsync(older.Candidate, older.ReleaseId, older.ReleaseRecordContentHash)).State);

        harness.Factory.RejectCurrentConfiguration = true;
        var incompatible = await ActivateLifecycleCandidateAsync(harness, older.Candidate, older.ReleaseId,
            older.ReleaseRecordContentHash, null);
        Assert.Equal(CommandDisposition.Rejected, incompatible.Outcome.Disposition);
        Assert.Null((await query.ReadCurrentAsync()).Record);
        Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
        harness.Factory.RejectCurrentConfiguration = false;
        var rollback = await ActivateLifecycleCandidateAsync(harness, older.Candidate, older.ReleaseId,
            older.ReleaseRecordContentHash, null);
        AssertAccepted(rollback.Outcome, "Explicit rollback through current checks");
        Assert.Equal(older.Candidate, (await query.ReadCurrentAsync()).Record?.Candidate);
        Assert.Equal(ReleasedRecipeLifecycleState.Retired, (await harness.Service<IRecipeLifecycleService>()
            .ReadReleaseAsync(active.Candidate, active.ReleaseId, active.ReleaseRecordContentHash)).State);
    }

    [Fact]
    public async Task V148_R08_NonactiveRetirementBetweenActivationAdmissionAndCommitWinsFinalAvailabilityFence()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            enableRecipeLifecycle: true);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var old = (await harness.Activations.ReadCurrentAsync()).Record!;
        var candidate = await ReleaseLifecycleCandidateAsync(harness);
        harness.Factory.HoldNextCreate();
        try
        {
            var activation = ActivateLifecycleCandidateAsync(harness, candidate.Reference, candidate.Record.ReleaseId,
                candidate.Record.ContentHash, old.Reference);
            await harness.Factory.NextCreateEntered.WaitAsync(TimeSpan.FromSeconds(10));
            var retire = new RetireReleasedRecipeCommand(Guid.NewGuid(), harness.Invocation(), candidate.Reference,
                candidate.Record.ReleaseId, candidate.Record.ContentHash, old.Reference, "V148 retire pending activation candidate");
            var grant = await ProductionGrantAsync(harness, Permission.RetireRecipe, retire.CorrelationId,
                retire.AuthorizationTarget, AuditedCommandKind.RetireReleasedRecipe);
            var retirement = await harness.Service<IRecipeLifecycleService>().RetireAsync(retire with
                { Invocation = retire.Invocation with { StepUpGrantId = grant } });
            AssertAccepted(retirement.Outcome, "Nonactive retirement wins writer order");
            Assert.Null(retirement.Record!.ClearedActive);
            harness.Factory.ReleaseNextCreate();
            var failed = await activation.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(CommandDisposition.Rejected, failed.Outcome.Disposition);
            Assert.Equal("RecipeRetired", failed.Outcome.ReasonCode);
            var current = await new SqliteRecipeActivationQuery(harness.Fixture.Options).ReadCurrentAsync();
            Assert.True(current.Available, current.ReasonCode);
            Assert.Equal(old.Reference, current.Record?.Reference);
            Assert.Equal(old.Candidate, (await harness.Runtime.GetSnapshotAsync()).ActiveRecipe);
        }
        finally { harness.Factory.ReleaseNextCreate(); }
    }

    private static async Task<ReleasedRecipe> ReleaseLifecycleCandidateAsync(ManualHarness harness, bool bindContract = true)
    {
        var saved = await harness.Fixture.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            ManualHarness.Encode(harness.Draft.Content), "V148 distinct later available version");
        Assert.True(saved.Saved, saved.ReasonCode);
        var draft = Assert.IsType<RecipeDraftRevision>(saved.Revision);
        var command = new ReleaseRecipeCommand(Guid.NewGuid(), harness.Invocation(), draft.DraftId, draft.Revision,
            draft.RevisionContentHash, harness.Fixture.Options.RecipeReleases!.Policy.Reference, "V148 release later version");
        var grant = await ProductionGrantAsync(harness, Permission.ReleaseRecipe, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe);
        var release = await harness.Service<IRecipeReleaseService>().ReleaseAsync(command with
            { Invocation = command.Invocation with { StepUpGrantId = grant } });
        AssertAccepted(release.Outcome, "Release distinct lifecycle candidate");
        if (bindContract)
        {
            var contracts = harness.Service<IPlcResultContractService>();
            var current = (await contracts.ReadCurrentAsync()).Revision!;
            var proposal = PlcResultContractTestSupport.Contract(harness.Factory.Descriptor.ResultSchema,
                version: "V148." + (current.Position + 1), frameworkReasons: PlcResultContract.ProductionFailureReasonCatalogV2);
            var change = new ChangePlcResultContractCommand(Guid.NewGuid(), harness.Invocation(), proposal,
                current.Reference, "V148 explicitly validate PLC contract against new release");
            var contractGrant = await ProductionGrantAsync(harness, Permission.ManagePlcResultContract,
                change.CorrelationId, change.AuthorizationTarget, AuditedCommandKind.ChangePlcResultContract);
            AssertAccepted(await harness.Runtime.SubmitAsync(change with
                { Invocation = change.Invocation with { StepUpGrantId = contractGrant } }), "Bind new available version");
        }
        return Assert.IsType<ReleasedRecipe>(release.Recipe);
    }

    private static async Task<RecipeActivationResult> ActivateLifecycleCandidateAsync(ManualHarness harness,
        RecipeReference recipe, Guid releaseId, string releaseHash, RecipeActivationReference? expected)
    {
        var command = new ActivateRecipeCommand(Guid.NewGuid(), harness.Invocation(), recipe, releaseId, releaseHash,
            expected, null, "V148 ordinary activation of available version");
        var grant = await ProductionGrantAsync(harness, Permission.ActivateRecipe, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.ActivateRecipe);
        return await harness.Service<IRecipeActivationService>().ActivateAsync(command with
            { Invocation = command.Invocation with { StepUpGrantId = grant } });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task V148_R09_CancellationAndActualRuntimeCommitClaimHaveOneDurableWinner(bool cancelBeforeClaim)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            enableRecipeLifecycle: true);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
        var active = (await harness.Activations.ReadCurrentAsync()).Record!;
        var command = await RetirementCommandAsync(harness, active);
        using var cancellation = new CancellationTokenSource();
        var claims = 0;
        async ValueTask<RecipeRetirementRuntimeLease> Reserve(RetireReleasedRecipeCommand request, CancellationToken token)
        {
            var owned = await runtime.ReserveRecipeRetirementAsync(request, token);
            if (!owned.Available) return owned;
            return new(owned.RuntimeEpoch, null, owned.Token, owned.Active, owned.DrainAsync,
                async value =>
                {
                    var commit = await owned.EnterCommitAsync(value);
                    // This internal fixture only places caller cancellation immediately
                    // beside the actual nonblocking Runtime claim; it invents no authority.
                    return new RecipeActivationCommitLease(commit.GetBlocker, commit.Dispose, () =>
                    {
                        if (cancelBeforeClaim) cancellation.Cancel();
                        var failure = commit.TryBeginCommit();
                        if (failure != "RecipeRetirementRuntimeBusy") Interlocked.Increment(ref claims);
                        if (!cancelBeforeClaim && failure is null) cancellation.Cancel();
                        return failure;
                    });
                }, owned.ClearCommitted, owned.CleanupAsync, owned.PublishTerminal, owned.Dispose);
        }
        var service = new RecipeLifecycleService(new SqliteRecipeLifecycleQuery(harness.Fixture.Options),
            harness.Activations, harness.Fixture.Authorization, harness.Fixture.Options, new(),
            () => runtime.GetSnapshotAsync(), Reserve);
        var result = await service.RetireAsync(command, cancellation.Token);
        Assert.Equal(1, claims);
        var history = await new SqliteRecipeLifecycleQuery(harness.Fixture.Options).QueryAsync(new());
        Assert.True(history.Available, history.ReasonCode);
        var current = await new SqliteRecipeActivationQuery(harness.Fixture.Options).ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        if (cancelBeforeClaim)
        {
            Assert.Equal(CommandDisposition.Rejected, result.Outcome.Disposition);
            Assert.Null(result.Record);
            Assert.Empty(history.Records);
            Assert.Equal(active.Reference, current.Record?.Reference);
        }
        else
        {
            AssertAccepted(result.Outcome, "Commit claim wins late cancellation");
            Assert.Single(history.Records);
            Assert.Equal(result.Record!.Reference, history.Records[0].Reference);
            Assert.Null(current.Record);
            Assert.Null((await runtime.GetSnapshotAsync()).ActiveRecipe);
        }
        Assert.False((await runtime.GetSnapshotAsync()).Ready);
    }

    [Fact]
    public async Task V148_R10_PendingMaintenanceArmRejectsRetirementWithoutCancellingReadyReceipt()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            enableRecipeLifecycle: true, productionArming: new ProductionArmStoreOptions(),
            productionCommunicationPolicy: new PlcCommunicationPolicy("V148.HeldReady", "1",
                TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(40), 2, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(15)),
            productionArmStatusBinding: V147ArmStatusBinding,
            productionArmMaintenance: new TestProductionArmMaintenanceProvider(),
            productionArmMaintenanceState: ProductionArmMaintenanceState.ManualArmRequired);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var active = (await harness.Activations.ReadCurrentAsync()).Record!;
        var command = await RetirementCommandAsync(harness, active);
        peer.HoldNextProductionReadyWrite();
        try
        {
            await ArmProductionAsync(harness);
            await peer.WaitForProductionReadyWriteHeldAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var result = await harness.Service<IRecipeLifecycleService>().RetireAsync(command);
            Assert.Equal(CommandDisposition.Rejected, result.Outcome.Disposition);
            Assert.Equal("RecipeRetirementProductionArmInProgress", result.Outcome.ReasonCode);
            Assert.Null(result.Record);
            Assert.Empty((await harness.Service<IRecipeLifecycleService>().QueryAsync(new())).Records);
            Assert.Equal(active.Reference, (await harness.Activations.ReadCurrentAsync()).Record?.Reference);
        }
        finally { peer.ReleaseProductionReadyWrite(); }
        var arm = await WaitForArmAttemptHistoryAsync(harness,
            page => page.Events.Any(value => value.Kind is ProductionArmEventKind.ReadyConfirmed or ProductionArmEventKind.Failed),
            "Original arm records its actual terminal outcome");
        Assert.True(arm.Events.Any(value => value.Kind == ProductionArmEventKind.ReadyConfirmed),
            string.Join(";", arm.Events.Select(value => value.Kind + ":" + value.ReasonCode)));
        await WaitProductionAsync(harness, state => state.Ready, "Original maintenance arm remains responsible for Ready");
    }

    [Fact]
    public async Task V148_R11_RetiredMappedReleaseKeepsExactMapAndReturnsExplicitPlcFailure()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)), enableRecipeLifecycle: true);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var active = (await harness.Activations.ReadCurrentAsync()).Record!;
        await EnableRecipeChangeAsync(harness, active);
        var selection = harness.Service<IRecipeSelectionService>();
        var before = (await selection.ReadCurrentAsync()).Revision!;
        var result = await harness.Service<IRecipeLifecycleService>().RetireAsync(await RetirementCommandAsync(harness, active));
        AssertAccepted(result.Outcome, "Retire mapped active version");
        var impact = Assert.Single(result.Record!.MapImpacts);
        Assert.Equal(before.Reference, impact.Selection);
        Assert.Equal(before.Map!.Reference, impact.Map);
        Assert.Equal(new uint[] { 7 }, impact.AffectedCodes);
        Assert.Equal(before.Reference, (await selection.ReadCurrentAsync()).Revision!.Reference);
        var prepared = harness.Factory.Created;
        var query = new SqliteRecipeSelectionQuery(harness.Fixture.Options);
        for (uint attempt = 0; attempt < 8; attempt++)
        {
            var sequence = 14811 + attempt * 1000;
            peer.RequestRecipeChange(sequence, 7);
            await WaitRecipeResponseAsync(harness, peer);
            var history = await query.QueryAsync(new RecipeChangeHistoryFilter(PageSize: 128));
            Assert.True(history.Available, history.ReasonCode);
            var decision = Assert.Single(history.Events, value => value.Request.RequestSequence == sequence &&
                value.Kind == RecipeChangeEventKind.DecisionCommitted);
            if (decision.ReasonCode == "RecipeActivationRuntimeBusy")
            {
                await CompleteRecipeChangeAsync(harness, peer, query);
                continue;
            }
            Assert.Equal((ushort)RecipeChangeOutcome.FailedActivation, peer.RecipeChangeResponse.Outcome);
            Assert.Equal((ushort)RecipeChangeReason.RecipeRetired, peer.RecipeChangeResponse.Reason);
            Assert.Equal("RecipeRetired", decision.ReasonCode);
            Assert.Equal(before.Map.Reference, decision.Request.SelectionMap);
            if (decision.Activation is { } failedActivation)
            {
                var failed = await harness.Activations.ReadAsync(failedActivation);
                Assert.True(failed.Available, failed.ReasonCode);
                Assert.False(failed.Record!.Outcome.Succeeded);
            }
            await CompleteRecipeChangeAsync(harness, peer, query);
            Assert.Equal(prepared, harness.Factory.Created);
            Assert.Equal(before.Reference, (await selection.ReadCurrentAsync()).Revision!.Reference);
            var current = await new SqliteRecipeActivationQuery(harness.Fixture.Options).ReadCurrentAsync();
            Assert.True(current.Available, current.ReasonCode);
            Assert.Null(current.Record);
            return;
        }
        Assert.Fail("Retired mapped request remained busy across eight explicit controller attempts");
    }

    [Fact]
    public async Task V148_R12_UnboundNewReleaseCannotCommitActivationAndPoisonReadableHistory()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            enableRecipeLifecycle: true);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var active = (await harness.Activations.ReadCurrentAsync()).Record!;
        var candidate = await ReleaseLifecycleCandidateAsync(harness, bindContract: false);
        var created = harness.Factory.Created;
        var rejected = await ActivateLifecycleCandidateAsync(harness, candidate.Reference, candidate.Record.ReleaseId,
            candidate.Record.ContentHash, active.Reference);
        Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
        Assert.Contains(rejected.Record!.Checks, value => value.ReasonCode == "RecipeActivationPlcReleaseBindingMissing");
        Assert.Equal(created, harness.Factory.Created);
        var current = await new SqliteRecipeActivationQuery(harness.Fixture.Options).ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Equal(active.Reference, current.Record?.Reference);
        Assert.NotEqual(AuditIntegrityState.Faulted, harness.Fixture.Store.Integrity?.State);
    }

    [Fact]
    public async Task V148_R13_NewMapCannotPublishRetiredTarget()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            recipeChangeBinding: new(500, 600, TimeSpan.FromSeconds(20)), enableRecipeLifecycle: true);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var active = (await harness.Activations.ReadCurrentAsync()).Record!;
        AssertAccepted((await harness.Service<IRecipeLifecycleService>().RetireAsync(
            await RetirementCommandAsync(harness, active))).Outcome, "Retire before new map");
        var selections = harness.Service<IRecipeSelectionService>();
        var before = (await selections.ReadCurrentAsync()).Revision;
        var change = new ChangeRecipeSelectionCommand(Guid.NewGuid(), harness.Invocation(),
            new("V148.Rejected.Selection", "1", RecipeSelectionMode.PlcRequestedActivation),
            new("V148.Rejected.Map", "1", new[] { new RecipeSelectionMapEntry(88, active.Candidate,
                active.ReleaseId, active.ReleaseRecordContentHash) }), before?.Reference, "Must reject retired target");
        var grant = await ProductionGrantAsync(harness, Permission.ManageRecipeSelectionMap, change.CorrelationId,
            change.AuthorizationTarget, AuditedCommandKind.ChangeRecipeSelection);
        var result = await selections.ChangeAsync(change with { Invocation = change.Invocation with { StepUpGrantId = grant } });
        Assert.Equal(CommandDisposition.Rejected, result.Outcome.Disposition);
        Assert.Equal("RecipeRetired", result.Outcome.ReasonCode);
        Assert.Null(result.Revision);
        var after = await selections.ReadCurrentAsync();
        Assert.True(after.Available, after.ReasonCode);
        Assert.Equal(before?.Reference, after.Revision?.Reference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V148_R14_ClosedLifecycleSourceCannotStartManualInspection(bool released)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: released ? peer : null, enableRecipeLifecycle: true);
        using var issuer = new ProductionTestIssuer();
        ManualRecipeSelection source;
        if (released)
        {
            await PrepareProductionAsync(harness, issuer);
            var active = (await harness.Activations.ReadCurrentAsync()).Record!;
            AssertAccepted((await harness.Service<IRecipeLifecycleService>().RetireAsync(
                await RetirementCommandAsync(harness, active))).Outcome, "Retire manual source");
            source = new(active.Candidate, active.ReleaseId, active.ReleaseRecordContentHash);
        }
        else
        {
            var abandon = new AbandonRecipeDraftCommand(Guid.NewGuid(), harness.Invocation(), harness.Draft.DraftId,
                harness.Draft.Revision, harness.Draft.RevisionContentHash, "Abandon manual source");
            var grant = await ProductionGrantAsync(harness, Permission.AbandonRecipeDraft, abandon.CorrelationId,
                abandon.AuthorizationTarget, AuditedCommandKind.AbandonRecipeDraft);
            AssertAccepted((await harness.Service<IRecipeLifecycleService>().AbandonAsync(abandon with
                { Invocation = abandon.Invocation with { StepUpGrantId = grant } })).Outcome, "Abandon manual source");
            source = ManualRecipeSelection.FromDraft(harness.Draft);
        }
        var created = harness.Factory.Created;
        var before = harness.Fixture.Scalar("SELECT COUNT(*) FROM manual_inspection_events;");
        var result = await harness.Runtime.SubmitAsync(new StartManualInspectionSessionCommand(Guid.NewGuid(),
            harness.Invocation(), source, null, "Closed history is never a manual inspection input"));
        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        Assert.Equal(released ? "RecipeRetired" : "RecipeDraftAbandoned", result.ReasonCode);
        Assert.Equal(created, harness.Factory.Created);
        Assert.Equal(before, harness.Fixture.Scalar("SELECT COUNT(*) FROM manual_inspection_events;"));
    }

    [Fact]
    public async Task V148_R15_AbandonmentAfterManualPreparationBlocksRunButAllowsGracefulExit()
    {
        await using var harness = await ManualHarness.CreateAsync(enableRecipeLifecycle: true);
        AssertAccepted(await harness.StartAsync(), "Prepare initially open draft");
        var ready = await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Manual source prepared before abandonment");
        var session = ready.SessionId!.Value;
        var command = new AbandonRecipeDraftCommand(Guid.NewGuid(), harness.Invocation(), harness.Draft.DraftId,
            harness.Draft.Revision, harness.Draft.RevisionContentHash, "Abandon prepared draft before another manual run");
        var grant = await ProductionGrantAsync(harness, Permission.AbandonRecipeDraft, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.AbandonRecipeDraft);
        var abandoned = await harness.Service<IRecipeLifecycleService>().AbandonAsync(command with
            { Invocation = command.Invocation with { StepUpGrantId = grant } });
        AssertAccepted(abandoned.Outcome, "Irreversible source closure during manual session");
        var count = harness.Fixture.Scalar("SELECT COUNT(*) FROM manual_inspection_events;");
        var result = await harness.RunAsync(session);
        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        Assert.Equal("RecipeDraftAbandoned", result.ReasonCode);
        Assert.Equal(count, harness.Fixture.Scalar("SELECT COUNT(*) FROM manual_inspection_events;"));
        AssertAccepted(await harness.ExitAsync(session, ManualInspectionExitMode.Graceful), "Cleanup stays authorized");
        await harness.WaitForSnapshotAsync(value => value.Phase == ManualInspectionSessionPhase.Closed,
            "Prepared manual source closes normally");
    }

    private static void ReleaseLifecycleCycle(ManualHarness harness, ModbusQualificationTestServer peer)
    {
        harness.Factory.ReleaseExecution();
        peer.AutoAcknowledge = true;
        peer.AutoClearAcknowledge = true;
        if (peer.RuntimeResultValid) peer.AcknowledgeResult();
        else if (peer.ControllerResultAck) peer.ResetResultAcknowledgement();
        peer.SetTrigger(false);
    }
}
