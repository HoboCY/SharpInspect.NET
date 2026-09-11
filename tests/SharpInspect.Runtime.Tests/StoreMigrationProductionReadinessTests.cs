using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Exercises five migration gates through the existing signed writer and runtime
/// fixtures. Virtual providers and loopback controllers supply development inputs;
/// these cases do not qualify a physical station.
/// </summary>
public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    [Trait("VerificationId", "V149_S09")]
    public async Task V149_S09_OpenProductionInspectionCycleBlocksMigrationUntilItsTerminalClosure()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);

        // A real admission through the production inspection writer reserves the
        // complete remaining handshake plus its fault/recovery tail.
        var admission = await BuildAdmissionAsync(harness);
        var admitted = await harness.Fixture.Store.AdmitProductionInspectionAsync(
            new ProductionInspectionAdmissionWriteRequest(admission),
            new StoreDeadline(TimeSpan.FromSeconds(10)));
        Assert.True(admitted.Committed, admitted.ReasonCode);
        Assert.True(ReadAuditReserve(harness.Fixture.Options) > 0);
        Assert.Equal("StoreMigrationProductionInspectionNotQuiescent",
            V149MigrationRefusalReason(harness.Fixture.Options, harness.Fixture.Store));

        // A fault-terminated cycle still owns its recovery tail.
        var fault = await AppendProductionEventAsync(harness, admission.InspectionId,
            ProductionInspectionEventKind.FaultTerminated, "V149MigrationFaultTerminated");
        Assert.True(fault.Committed, fault.ReasonCode);
        Assert.Equal("StoreMigrationProductionInspectionNotQuiescent",
            V149MigrationRefusalReason(harness.Fixture.Options, harness.Fixture.Store));

        // RecoveryRequired is explicitly not a terminal state.
        var recoveryRequired = await AppendProductionEventAsync(harness, admission.InspectionId,
            ProductionInspectionEventKind.RecoveryRequired, "V149MigrationRecoveryRequired");
        Assert.True(recoveryRequired.Committed, recoveryRequired.ReasonCode);
        Assert.Equal("StoreMigrationProductionInspectionNotQuiescent",
            V149MigrationRefusalReason(harness.Fixture.Options, harness.Fixture.Store));
        Assert.True(ReadAuditReserve(harness.Fixture.Options) > 0);

        // RecoveryCompleted is one of the two stored closures for an inspection
        // cycle (the other is AcknowledgementReset). Only the genuine terminal row
        // releases the writer's own reservation projection to zero rows.
        var completed = await AppendProductionEventAsync(harness, admission.InspectionId,
            ProductionInspectionEventKind.RecoveryCompleted, "V149MigrationRecoveryCompleted");
        Assert.True(completed.Committed, completed.ReasonCode);
        Assert.Equal(0, ReadAuditReserve(harness.Fixture.Options));
        V149RequireMigrationQuiescent(harness.Fixture.Options, harness.Fixture.Store);
    }

    [Fact]
    [Trait("VerificationId", "V149_S10")]
    public async Task V149_S10_OpenStationQualificationSessionBlocksMigrationUntilItIsRestoredAndClosed()
    {
        // The facility is the deterministic virtual provider; the blocked
        // stimulus holds the session open at its ready phase.
        await using var harness = await QualificationHarness.CreateAsync(blockStimulus: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "V149 station qualification start");
        var ready = await harness.WaitForSnapshotAsync(
            snapshot => snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "V149 station qualification did not reach its stimulus wait");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        Assert.True(V149ReadStationQualificationReserve(harness.Fixture.Options) > 0);
        Assert.Equal("StoreMigrationStationQualificationNotQuiescent",
            V149MigrationRefusalReason(harness.Fixture.Options, harness.Fixture.Store));

        // The abort retirement writes the real Restoring readback rows and the
        // terminal Closed row that releases the reserved facility tail.
        AssertAccepted(await harness.Runtime.SubmitAsync(harness.ExitCommand(sessionId, abort: true)),
            "V149 station qualification abort");
        var closed = await harness.WaitForSnapshotAsync(
            snapshot => snapshot.SessionId == sessionId &&
                snapshot.Phase == StationQualificationSessionPhase.Closed,
            "V149 station qualification did not restore and close");
        Assert.Equal(StationQualificationRestorationState.Restored, closed.Restoration);
        Assert.Equal(0, V149ReadStationQualificationReserve(harness.Fixture.Options));
        V149RequireMigrationQuiescent(harness.Fixture.Options, harness.Fixture.Store);
    }

    [Fact]
    [Trait("VerificationId", "V149_S11")]
    public async Task V149_S11_PendingQualificationCycleRunBlocksMigrationUntilItsAckReset()
    {
        // The controller holds its acknowledgement, so the accepted run stays
        // persisted and unresolved long enough to observe the durable state.
        await using var controller = ModbusQualificationTestServer.Start();
        controller.AutoAcknowledge = false;
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1,
            profileFactory: snapshot => controller.CreateProfile(snapshot,
                acknowledgementTimeout: TimeSpan.FromSeconds(30)));

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "V149 qualification cycle start");
        var ready = await harness.WaitForSnapshotAsync(
            snapshot => snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "V149 qualification cycle session did not reach readiness");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(41, 7);
        await WaitForQualificationCyclePageAsync(harness, sessionId,
            page => page.Events.Any(value => value.Kind == QualificationCycleEventKind.Admitted),
            "V149 qualification cycle admission was not queryable");
        await controller.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await WaitForQualificationCyclePageAsync(harness, sessionId,
            page => page.Events.Any(value => value.Kind == QualificationCycleEventKind.CoreCommitted),
            "V149 qualification cycle core was not queryable");

        // The run is durable and still awaits its reset, so the cycle ledger's own
        // projection reports outstanding work even though the station session is
        // already recovery-blocked at this point.
        var pending = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(SessionId: sessionId, PageSize: 128));
        Assert.True(pending.Available, pending.ReasonCode);
        Assert.True(pending.RecoveryRequired, pending.ReasonCode);
        Assert.NotNull(pending.PendingEvent);
        Assert.NotEqual(QualificationCycleEventKind.AckReset, pending.PendingEvent!.Kind);
        Assert.True(await ReadCycleAuditReserveForTestAsync(harness.Fixture) > 0);

        // The aggregate stops at the still-open station session. Enter the cycle
        // gate separately on the same valid history so its refusal is covered too.
        Assert.Equal("StoreMigrationStationQualificationNotQuiescent",
            V149MigrationRefusalReason(harness.Fixture.Options, harness.Fixture.Store));
        var cycleRefusal = Assert.Throws<InvalidOperationException>(() =>
            V149RequireQualificationCycleQuiescent(harness.Fixture.Options, harness.Fixture.Store));
        Assert.Equal("StoreMigrationQualificationCycleNotQuiescent", cycleRefusal.Message);

        // The acknowledged reset resolves the run, closes the session and leaves
        // both ledgers with zero outstanding work.
        controller.AcknowledgeResult();
        await controller.WaitForAckLowAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var closed = await harness.WaitForSnapshotAsync(
            snapshot => snapshot.SessionId == sessionId &&
                snapshot.Phase == StationQualificationSessionPhase.Closed,
            "V149 qualification cycle session did not close after its reset");
        Assert.Equal(StationQualificationRestorationState.Restored, closed.Restoration);
        var closedPage = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(SessionId: sessionId, PageSize: 128));
        Assert.True(closedPage.Available, closedPage.ReasonCode);
        Assert.Contains(closedPage.Events, value => value.Kind == QualificationCycleEventKind.AckReset);
        Assert.False(closedPage.RecoveryRequired, closedPage.ReasonCode);
        Assert.Equal(0, await ReadCycleAuditReserveForTestAsync(harness.Fixture));
        V149RequireQualificationCycleQuiescent(harness.Fixture.Options, harness.Fixture.Store);
        V149RequireMigrationQuiescent(harness.Fixture.Options, harness.Fixture.Store);
    }

    [Fact]
    [Trait("VerificationId", "V149_S12")]
    public async Task V149_S12_AdmittedRecipeActivationBlocksMigrationUntilItsTerminalFact()
    {
        await using var harness = await RecipeActivationServiceTests.ActivationHarness.CreateAsync();
        var command = await harness.AuthorizedActivationCommand();
        var epoch = (await harness.Runtime.GetSnapshotAsync()).RuntimeEpoch;
        var checks = new[]
        {
            new RecipeActivationCheck("V149.A01", "MigrationReadinessQuiescence",
                RecipeActivationCheckStatus.Passed, "V149MigrationQuiescent")
        };

        // A real admission through the activation writer: the release binding,
        // signed authorization identity event, accepted command fact and the
        // admitted record commit in one transaction. The qualification evidence
        // is the caller's fixture list, never production authority.
        var admission = await harness.Authorization.AdmitRecipeActivationAsync(command, epoch,
            Guid.NewGuid(), RecipeActivationEvidenceKind.LocalAuthority, checks, null, () => null,
            new StoreDeadline(TimeSpan.FromSeconds(10)), CancellationToken.None);
        Assert.Equal(CommandDisposition.Accepted, admission.Outcome.Disposition);
        Assert.Equal("RecipeActivationAdmitted", admission.Outcome.ReasonCode);
        var admitted = Assert.IsType<RecipeActivationRecord>(admission.Record);
        Assert.Equal(RecipeActivationOutcomeState.Admitted, admitted.Outcome.State);
        Assert.NotNull(V149ReadPendingActivationAdmission(harness.Options, harness.Store));
        Assert.Equal("StoreMigrationRecipeActivationNotQuiescent",
            V149MigrationRefusalReason(harness.Options, harness.Store));

        // The terminal fact of the same attempt is the only closure; it commits
        // its own signed identity/command pair and a terminal record.
        var failed = await harness.Authorization.FinalizeRecipeActivationFailureAsync(command, epoch,
            admitted, checks,
            new RecipeActivationRestoration(RecipeActivationRestorationState.NotRequired,
                "RecipeActivationHardwareUntouched"),
            RecipeActivationOutcomeState.Failed, "V149MigrationActivationFailed",
            new StoreDeadline(TimeSpan.FromSeconds(10)));
        Assert.Equal(CommandDisposition.Rejected, failed.Outcome.Disposition);
        Assert.Equal(AuditPersistence.Persisted, failed.Outcome.Audit);
        Assert.Null(V149ReadPendingActivationAdmission(harness.Options, harness.Store));
        V149RequireMigrationQuiescent(harness.Options, harness.Store);
    }

    [Fact]
    [Trait("VerificationId", "V149_S13")]
    public async Task V149_S13_RecipeChangeEpisodeBlocksMigrationUntilItsHandshakeOrFaultClosure()
    {
        await using var fixture = await V149CreateRecipeChangeFixtureAsync();
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);

        // An observed request alone reserves decision, response, acknowledgement,
        // clear, reset and one fault fact.
        var handshake = V149RecipeChangeRequest(epoch: 11, sequence: 101, code: 3);
        var observed = await V149AppendRecipeChangeAsync(fixture, handshake,
            RecipeChangeEventKind.RequestObserved, null, null, "RecipeChangeRequestObserved");
        Assert.True(observed.Committed, observed.ReasonCode);
        Assert.Equal(6, V149ReadRecipeChangeReserve(fixture.Options));
        Assert.Equal("StoreMigrationRecipeChangeNotQuiescent",
            V149MigrationRefusalReason(fixture.Options, fixture.Store));

        // The normal controller handshake ending in the zeroed reset is the
        // legitimate closure for a rejected decision.
        foreach (var kind in new[]
        {
            RecipeChangeEventKind.DecisionCommitted, RecipeChangeEventKind.ResponsePublished,
            RecipeChangeEventKind.AcknowledgementObserved, RecipeChangeEventKind.ResponseCleared,
            RecipeChangeEventKind.ResetObserved
        })
        {
            var written = await V149AppendRecipeChangeAsync(fixture, handshake, kind,
                RecipeChangeOutcome.RejectedUnknownCode, RecipeChangeReason.LocalOperatorOnly,
                "RecipeChangeLocalOperatorOnly");
            Assert.True(written.Committed, kind + ":" + written.ReasonCode);
        }

        Assert.Equal(0, V149ReadRecipeChangeReserve(fixture.Options));
        V149RequireMigrationQuiescent(fixture.Options, fixture.Store);

        // A second episode: a bare protocol fault is not closure, because the
        // decision slot survives until the decision itself is recorded.
        var interrupted = V149RecipeChangeRequest(epoch: 12, sequence: 102, code: 4);
        var secondObserved = await V149AppendRecipeChangeAsync(fixture, interrupted,
            RecipeChangeEventKind.RequestObserved, null, null, "RecipeChangeRequestObserved");
        Assert.True(secondObserved.Committed, secondObserved.ReasonCode);
        var fault = await V149AppendRecipeChangeAsync(fixture, interrupted,
            RecipeChangeEventKind.ProtocolFault, RecipeChangeOutcome.FailedActivation,
            RecipeChangeReason.CommunicationLost, "RecipeChangeInterruptedByRestart");
        Assert.True(fault.Committed, fault.ReasonCode);
        Assert.Equal(1, V149ReadRecipeChangeReserve(fixture.Options));
        Assert.Equal("StoreMigrationRecipeChangeNotQuiescent",
            V149MigrationRefusalReason(fixture.Options, fixture.Store));

        // DecisionCommitted together with ProtocolFault is the recipe change
        // ledger's own closed outcome for an interrupted episode; it is not a
        // fabricated response, acknowledgement or reset.
        var decision = await V149AppendRecipeChangeAsync(fixture, interrupted,
            RecipeChangeEventKind.DecisionCommitted, RecipeChangeOutcome.FailedActivation,
            RecipeChangeReason.CommunicationLost, "RecipeChangeInterruptedByRestart");
        Assert.True(decision.Committed, decision.ReasonCode);
        Assert.Equal(0, V149ReadRecipeChangeReserve(fixture.Options));
        V149RequireMigrationQuiescent(fixture.Options, fixture.Store);
    }

    /// <summary>
    /// Runs the real readiness replay on an isolated read-only SQLite snapshot of
    /// the durable database, exactly as the maintenance session does.
    /// </summary>
    private static void V149RequireMigrationQuiescent(ProductionStoreOptions options,
        SqliteCommandStore store)
    {
        using var read = SqliteNative.Open(options.DatabasePath, readOnly: true);
        store.RequireMigrationQuiescent(read.Handle!, new StoreDeadline(TimeSpan.FromSeconds(10)));
    }

    // The earlier station gate masks this gate for a legitimately pending cycle.
    // Reflection enters the actual private predicate without changing its visibility
    // or removing required station configuration from the valid source history.
    private static void V149RequireQualificationCycleQuiescent(ProductionStoreOptions options,
        SqliteCommandStore store)
    {
        using var read = SqliteNative.Open(options.DatabasePath, readOnly: true);
        var method = typeof(SqliteCommandStore).GetMethod("RequireQualificationCycleQuiescent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        try { method!.Invoke(store, new object[] { read.Handle!, new StoreDeadline(TimeSpan.FromSeconds(10)) }); }
        catch (System.Reflection.TargetInvocationException exception)
            when (exception.InnerException is InvalidOperationException reason)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(reason).Throw(); throw; }
    }

    /// <summary>Returns the exact StoreMigration reason the replay refuses with.</summary>
    private static string V149MigrationRefusalReason(ProductionStoreOptions options,
        SqliteCommandStore store)
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => V149RequireMigrationQuiescent(options, store));
        return refusal.Message;
    }

    private static long V149ReadStationQualificationReserve(ProductionStoreOptions options)
    {
        using var read = SqliteNative.Open(options.DatabasePath, readOnly: true);
        return SqliteCommandStore.ReadStationQualificationAuditReserve(read.Handle!,
            new StoreDeadline(TimeSpan.FromSeconds(10)));
    }

    private static RecipeActivationRecord? V149ReadPendingActivationAdmission(
        ProductionStoreOptions options, SqliteCommandStore store)
    {
        using var read = SqliteNative.Open(options.DatabasePath, readOnly: true);
        return store.ReadRecipeActivationCommandState(read.Handle!,
            new StoreDeadline(TimeSpan.FromSeconds(10))).PendingAdmission;
    }

    private static long V149ReadRecipeChangeReserve(ProductionStoreOptions options)
    {
        using var read = SqliteNative.Open(options.DatabasePath, readOnly: true);
        return SqliteCommandStore.ReadRecipeChangeAuditReserve(read.Handle!,
            new StoreDeadline(TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// The real signed handshake ledger requires the release, activation and PLC
    /// communication stores the runtime keeps beside the selection ledger; the
    /// requests themselves are fixture inputs, not production authority.
    /// </summary>
    private static Task<RecipeDraftStorageTests.Fixture> V149CreateRecipeChangeFixtureAsync() =>
        RecipeDraftStorageTests.Fixture.CreateAsync(
            recipeReleases: new RecipeReleaseStoreOptions(new RecipeGovernancePolicy(
                "V149.Migration.Release", "1", RecipeGovernanceMode.SingleApproverRelease)),
            plcResultContracts: new PlcResultContractStoreOptions(),
            cameraSetup: new CameraSetupStoreOptions(),
            recipeActivations: new RecipeActivationStoreOptions(),
            plcCommunication: new PlcCommunicationStoreOptions(),
            recipeSelections: new RecipeSelectionStoreOptions());

    private static RecipeChangeRequestEvidence V149RecipeChangeRequest(uint epoch, uint sequence,
        uint code) => new(Guid.NewGuid(), new string('A', 64),
        new RecipeContractReference("V149.Protocol", "1", new string('B', 64)), epoch, sequence, code,
        null, RecipeSelectionPolicy.Default.Reference, null, null, DateTimeOffset.UtcNow);

    private static ValueTask<RecipeChangeWriteResult> V149AppendRecipeChangeAsync(
        RecipeDraftStorageTests.Fixture fixture, RecipeChangeRequestEvidence request,
        RecipeChangeEventKind kind, RecipeChangeOutcome? outcome, RecipeChangeReason? reason,
        string reasonCode) => fixture.Store.AppendRecipeChangeEventAsync(request, kind, outcome,
        reason, reasonCode, null, new StoreDeadline(TimeSpan.FromSeconds(10)), CancellationToken.None);
}
