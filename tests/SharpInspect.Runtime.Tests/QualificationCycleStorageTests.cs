using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V140_S20_ProtocolRejectionDoesNotHidePendingRun()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.AutoAcknowledge = false;
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1,
            profileFactory: snapshot => controller.CreateProfile(snapshot,
                acknowledgementTimeout: TimeSpan.FromSeconds(15)));

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "qualification cycle pending-run start");
        var ready = await harness.WaitForSnapshotAsync(
            value => value.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "qualification cycle did not reach readiness");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);

        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(41, 7);

        var admittedPage = await WaitForQualificationCyclePageAsync(harness, sessionId,
            value => value.Events.Any(eventValue =>
                eventValue.Kind == QualificationCycleEventKind.Admitted),
            "qualification cycle admission was not queryable");
        Assert.Contains(admittedPage.Events, value =>
            value.Kind == QualificationCycleEventKind.Admitted);

        await controller.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(20));

        var corePage = await WaitForQualificationCyclePageAsync(harness, sessionId,
            value => value.Events.Any(eventValue =>
                eventValue.Kind == QualificationCycleEventKind.CoreCommitted),
            "qualification cycle core was not queryable");
        Assert.Contains(corePage.Events, value =>
            value.Kind == QualificationCycleEventKind.CoreCommitted);

        // The result is held awaiting Ack, so the observer can durably record a
        // second fresh controller key while the first RunId is still pending.
        controller.RaiseTrigger(41, 8);
        var page = await WaitForQualificationCyclePageAsync(harness, sessionId,
            value => value.Events.Any(eventValue =>
                eventValue.Kind == QualificationCycleEventKind.ProtocolRequestRejected),
            "qualification cycle protocol rejection was not persisted");
        Assert.True(page.RecoveryRequired, page.ReasonCode);
        var pending = Assert.IsType<QualificationCycleEvent>(page.PendingEvent);
        Assert.NotEqual(QualificationCycleEventKind.ProtocolRequestRejected, pending.Kind);
        Assert.NotNull(pending.RunId);
        Assert.Contains(page.Events, value =>
            value.Kind == QualificationCycleEventKind.ProtocolRequestRejected &&
            value.RunId?.Value == pending.RunId?.Value);

        // The duplicate request changed the server's current controller key to
        // (41,8).  Ack identity is still the accepted run's original key, so
        // restore (41,7) while the input is low before acknowledging.  This
        // also makes the low observation deterministic instead of relying on
        // the next sample after the duplicate rejection.
        controller.ArmControllerLowObservation();
        controller.SetControllerCycle(41, 7);
        controller.SetTrigger(false);
        await controller.WaitForControllerLowObservedAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.AcknowledgeResult();
        await controller.WaitForAckLowAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await harness.WaitForSnapshotAsync(
            value => value.SessionId == sessionId &&
                value.Phase == StationQualificationSessionPhase.Closed,
            "qualification cycle pending-run cleanup did not close");

        var closedPage = await WaitForQualificationCyclePageAsync(harness, sessionId,
            value => value.Events.Any(eventValue =>
                eventValue.Kind == QualificationCycleEventKind.AckReset),
            "qualification cycle AckReset was not queryable");
        Assert.False(closedPage.RecoveryRequired, closedPage.ReasonCode);
        Assert.Null(closedPage.PendingEvent);
        Assert.Equal(0, await ReadCycleAuditReserveForTestAsync(harness.Fixture));

        var coldOptions = harness.Fixture.CreateColdSnapshotOptions();
        var coldPage = await new SqliteQualificationCycleHistoryQuery(coldOptions)
            .QueryAsync(new(SessionId: sessionId, PageSize: 128));
        Assert.True(coldPage.Available, coldPage.ReasonCode);
        Assert.Contains(coldPage.Events, value =>
            value.Kind == QualificationCycleEventKind.Admitted);
        Assert.Contains(coldPage.Events, value =>
            value.Kind == QualificationCycleEventKind.CoreCommitted);
        Assert.Contains(coldPage.Events, value =>
            value.Kind == QualificationCycleEventKind.AckReset);
        Assert.False(coldPage.RecoveryRequired, coldPage.ReasonCode);
        Assert.Null(coldPage.PendingEvent);
        Assert.Equal(0, await ReadCycleAuditReserveForTestAsync(coldOptions));
    }

    [Fact]
    public async Task V140_S21_TamperedCyclePolicyBindingFailsColdRead()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1,
            profileFactory: snapshot => controller.CreateProfile(snapshot));

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "qualification cycle tamper start");
        var ready = await harness.WaitForSnapshotAsync(
            value => value.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "qualification cycle tamper session did not reach readiness");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(41, 7);
        var admittedPage = await WaitForQualificationCyclePageAsync(harness, sessionId,
            value => value.Events.Any(eventValue =>
                eventValue.Kind == QualificationCycleEventKind.Admitted),
            "qualification cycle tamper admission was not queryable");
        Assert.Contains(admittedPage.Events, value =>
            value.Kind == QualificationCycleEventKind.Admitted);
        await controller.WaitForAckLowAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await harness.WaitForSnapshotAsync(
            value => value.SessionId == sessionId &&
                value.Phase == StationQualificationSessionPhase.Closed,
            "qualification cycle tamper session did not close");

        var liveCorePage = await WaitForQualificationCyclePageAsync(harness, sessionId,
            value => value.Events.Any(eventValue =>
                eventValue.Kind == QualificationCycleEventKind.CoreCommitted),
            "qualification cycle tamper core was not queryable");
        Assert.Contains(liveCorePage.Events, value =>
            value.Kind == QualificationCycleEventKind.CoreCommitted);
        var liveClosedPage = await WaitForQualificationCyclePageAsync(harness, sessionId,
            value => value.Events.Any(eventValue =>
                eventValue.Kind == QualificationCycleEventKind.AckReset),
            "qualification cycle tamper AckReset was not queryable");
        Assert.False(liveClosedPage.RecoveryRequired, liveClosedPage.ReasonCode);
        Assert.Null(liveClosedPage.PendingEvent);
        Assert.Equal(0, await ReadCycleAuditReserveForTestAsync(harness.Fixture));

        // Query an isolated cold backup.  The live database is never modified
        // while its runtime writer is active; the tamper is applied only to
        // this backup, which has no writer or runtime owner.
        var coldOptions = harness.Fixture.CreateColdSnapshotOptions();
        var query = new SqliteQualificationCycleHistoryQuery(coldOptions);
        var before = await query.QueryAsync(new(SessionId: sessionId, PageSize: 128));
        Assert.True(before.Available, before.ReasonCode);
        var core = Assert.Single(before.Events,
            value => value.Kind == QualificationCycleEventKind.CoreCommitted);
        var policy = Assert.IsType<TraceStoragePolicySnapshot>(core.PolicySnapshot);
        Assert.Empty(policy.Publication.Policy.RequiredRoutes);

        CycleStorageTamperPolicyHash(coldOptions.DatabasePath,
            core.Position, out var originalColumnValue);
        try
        {
            var tampered = await query.ReadAsync(sessionId);
            Assert.False(tampered.Available);
            Assert.Contains("QualificationCycle", tampered.ReasonCode,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CycleStorageRestorePolicyHash(coldOptions.DatabasePath,
                core.Position, originalColumnValue!);
        }

        var restored = await query.ReadAsync(sessionId);
        Assert.True(restored.Available, restored.ReasonCode);
        var restoredPage = await query.QueryAsync(new(SessionId: sessionId, PageSize: 128));
        Assert.True(restoredPage.Available, restoredPage.ReasonCode);
        Assert.Contains(restoredPage.Events, value =>
            value.Kind == QualificationCycleEventKind.Admitted);
        Assert.Contains(restoredPage.Events, value =>
            value.Kind == QualificationCycleEventKind.CoreCommitted);
        Assert.Contains(restoredPage.Events, value =>
            value.Kind == QualificationCycleEventKind.AckReset);
        Assert.False(restoredPage.RecoveryRequired, restoredPage.ReasonCode);
        Assert.Null(restoredPage.PendingEvent);
        Assert.Equal(0, await ReadCycleAuditReserveForTestAsync(coldOptions));
    }

    [Fact]
    public async Task V140_S22_AckTimeoutRemainsRecoveryRequired()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.AutoAcknowledge = false;
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1, allowDisposeFailure: true,
            profileFactory: snapshot => controller.CreateProfile(snapshot,
                acknowledgementTimeout: TimeSpan.FromMilliseconds(250)));

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "qualification cycle timeout start");
        var ready = await harness.WaitForSnapshotAsync(
            value => value.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "qualification cycle timeout session did not reach readiness");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(41, 7);
        await controller.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(20));

        await harness.WaitForSnapshotAsync(
            value => value.SessionId == sessionId &&
                value.Phase == StationQualificationSessionPhase.RecoveryBlocked,
            "qualification cycle Ack timeout did not block recovery");

        var page = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(SessionId: sessionId, PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Contains(page.Events, value =>
            value.Kind is QualificationCycleEventKind.AckTimeout or
                QualificationCycleEventKind.CycleFaultTerminated);
        Assert.True(page.RecoveryRequired, page.ReasonCode);
        Assert.NotNull(page.PendingEvent);
        Assert.NotEqual(QualificationCycleEventKind.AckReset, page.PendingEvent!.Kind);
    }

    [Fact]
    public async Task V140_S23_EndpointAndControllerKeyCannotBeReusedAcrossSessions()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1, allowDisposeFailure: true,
            profileFactory: snapshot => controller.CreateProfile(snapshot));

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "qualification cycle first session start");
        var first = await harness.WaitForSnapshotAsync(
            value => value.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "qualification cycle first session did not reach readiness");
        var firstSessionId = Assert.IsType<Guid>(first.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(41, 7);
        await controller.WaitForAckLowAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await harness.WaitForSnapshotAsync(
            value => value.SessionId == firstSessionId &&
                value.Phase == StationQualificationSessionPhase.Closed,
            "qualification cycle first session did not close");

        // The first observer is retired with the closed session.  Arm the
        // low-sample latch while the input is still high, then lower the input
        // before the second session connects.  The second connection must
        // produce a fresh Ready write and a real low sample; the original
        // one-shot Ready task is not a valid synchronization point here.
        var readyWritesBeforeSecond = controller.ReadyWriteCount;
        controller.ArmControllerLowObservation();
        controller.SetTrigger(false);

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "qualification cycle second session start");
        var secondControllerLowObserved = false;
        try
        {
            await WaitForReadyWriteCountAsync(controller, readyWritesBeforeSecond + 1,
                "qualification cycle second Ready edge was not written");
            await controller.WaitForControllerLowObservedAsync().WaitAsync(TimeSpan.FromSeconds(10));
            secondControllerLowObserved = true;
        }
        catch (Exception exception)
        {
            throw new XunitException(
                "qualification cycle second Ready synchronization failed; " +
                $"ReadyCount={controller.ReadyWriteCount};Connections={controller.ConnectionCount};" +
                $"ControllerLow={secondControllerLowObserved};" +
                $"{await DescribeQualificationSnapshotAsync(harness)};" +
                $"Error={exception.Message}");
        }
        StationQualificationSessionSnapshot second;
        try
        {
            second = await harness.WaitForSnapshotAsync(
                value => value.SessionId != firstSessionId &&
                    value.Phase == StationQualificationSessionPhase.ReadyForStimulus,
                "qualification cycle second session did not reach readiness");
        }
        catch (Exception exception)
        {
            throw new XunitException(
                "qualification cycle second Ready snapshot failed; " +
                $"ReadyCount={controller.ReadyWriteCount};Connections={controller.ConnectionCount};" +
                $"ControllerLow={secondControllerLowObserved};" +
                $"{await DescribeQualificationSnapshotAsync(harness)};" +
                $"Error={exception.Message}");
        }
        var secondSessionId = Assert.IsType<Guid>(second.SessionId);
        controller.RaiseTrigger(41, 7);
        try
        {
            await harness.WaitForSnapshotAsync(
                value => value.SessionId == secondSessionId &&
                    value.Phase is StationQualificationSessionPhase.Closed or
                        StationQualificationSessionPhase.RecoveryBlocked,
                "qualification cycle duplicate-key session did not retire");
        }
        catch (Exception exception)
        {
            throw new XunitException(
                "qualification cycle duplicate-key retirement failed; " +
                $"ReadyCount={controller.ReadyWriteCount};Connections={controller.ConnectionCount};" +
                $"ControllerLow={secondControllerLowObserved};" +
                $"{await DescribeQualificationSnapshotAsync(harness)};" +
                $"{await DescribeQualificationCycleAsync(harness, secondSessionId)};" +
                $"Error={exception.Message}");
        }

        var page = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        var admissions = page.Events.Where(value =>
            value.Kind == QualificationCycleEventKind.Admitted &&
            value.EndpointBindingHash == harness.Plan.TransientControllerConfiguration.EndpointBindingHash &&
            value.ControllerEpoch == 41 && value.CycleSequence == 7).ToArray();
        Assert.Single(admissions);
        Assert.Contains(page.Events, value =>
            value.Kind == QualificationCycleEventKind.ProtocolRequestRejected &&
            value.SessionId == secondSessionId);
    }

    [Fact]
    public async Task V140_S24_CycleReserveLeavesAcceptedRunClosableAfterGenericAuditExhaustion()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.AutoAcknowledge = false;
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(
            // The smallest valid audit policy keeps this pressure test bounded:
            // setup and one accepted run fit, but ordinary writes exhaust the
            // remaining budget within the deliberately held Ack window.
            maximumRuns: 1, maximumAuditEntries: 202,
            profileFactory: snapshot => controller.CreateProfile(snapshot,
                acknowledgementTimeout: TimeSpan.FromSeconds(30)));

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "qualification cycle reserve start");
        var ready = await harness.WaitForSnapshotAsync(
            value => value.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "qualification cycle reserve session did not reach readiness");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(41, 7);
        await controller.WaitForResultValidAsync().WaitAsync(TimeSpan.FromSeconds(20));

        var reserve = await ReadCycleAuditReserveForTestAsync(harness.Fixture);
        Assert.True(reserve > 0);
        var exhausted = await ExhaustQualificationGenericAuditCapacityAsync(harness);
        Assert.Contains("CapacityExceeded", exhausted.ReasonCode,
            StringComparison.OrdinalIgnoreCase);

        controller.AcknowledgeResult();
        await controller.WaitForAckLowAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var closed = await harness.WaitForSnapshotAsync(
            value => value.SessionId == sessionId &&
                value.Phase == StationQualificationSessionPhase.Closed,
            "qualification cycle reserve was consumed before terminal close");
        Assert.Equal(StationQualificationRestorationState.Restored, closed.Restoration);

        var page = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(SessionId: sessionId, PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Contains(page.Events, value => value.Kind == QualificationCycleEventKind.AckReset);
        Assert.False(page.RecoveryRequired);
        Assert.Equal(0, await ReadCycleAuditReserveForTestAsync(harness.Fixture));
    }

    [Fact]
    public async Task V140_S25_IllegalPredecessorFailsTransitionValidator()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1,
            profileFactory: snapshot => controller.CreateProfile(snapshot));

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "qualification cycle transition-validator start");
        var ready = await harness.WaitForSnapshotAsync(
            value => value.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "qualification cycle transition-validator session did not reach readiness");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(41, 7);
        await controller.WaitForAckLowAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await harness.WaitForSnapshotAsync(
            value => value.SessionId == sessionId &&
                value.Phase == StationQualificationSessionPhase.Closed,
            "qualification cycle transition-validator session did not close");

        var cyclePage = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(SessionId: sessionId, PageSize: 128));
        Assert.True(cyclePage.Available, cyclePage.ReasonCode);
        var core = Assert.Single(cyclePage.Events,
            value => value.Kind == QualificationCycleEventKind.CoreCommitted);

        var stationPage = await harness.History.QueryAsync(
            new StationQualificationHistoryFilter(SessionId: sessionId, PageSize: 128));
        Assert.True(stationPage.Available, stationPage.ReasonCode);
        var qualification = Assert.Single(stationPage.Events,
            value => value.Position == core.QualificationPosition);

        // Copy the real successful ledger events, then remove the admission
        // predecessor while leaving the candidate and all immutable fields
        // untouched.  The validator must reject this illegal jump with its
        // stable admission-missing reason.
        var invalidPrior = cyclePage.Events
            .Where(value => value.Position != core.Position &&
                value.Kind != QualificationCycleEventKind.Admitted)
            .ToArray();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqliteCommandStore.ValidateQualificationCycleTransition(
                core, invalidPrior, qualification, policyAtAdmission: null));
        Assert.Equal("QualificationCycleAdmissionMissing", exception.Message);
    }

    [Fact]
    public async Task V140_S26_CycleRowCapacityRejectsAdmissionAfterSessionReady()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1, allowDisposeFailure: true,
            qualificationCycleOptions: new QualificationCycleStoreOptions
            {
                MaximumEntries = 1
            },
            profileFactory: snapshot => controller.CreateProfile(snapshot));

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "qualification cycle row-capacity session start");
        var ready = await harness.WaitForSnapshotAsync(
            value => value.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "qualification cycle row-capacity session did not reach readiness");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(41, 7);

        await AssertQualificationCycleAdmissionRollbackAsync(harness, controller, sessionId,
            "qualification cycle row-capacity admission did not fail closed");
    }

    [Fact]
    public async Task V140_S27_CycleByteCapacityRejectsAdmissionAfterSessionReady()
    {
        const int singlePayloadBudget = 64 * 1024;
        await using var controller = ModbusQualificationTestServer.Start();
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1, allowDisposeFailure: true,
            qualificationCycleOptions: new QualificationCycleStoreOptions
            {
                MaximumPayloadBytes = singlePayloadBudget,
                MaximumTotalBytes = singlePayloadBudget
            },
            profileFactory: snapshot => controller.CreateProfile(snapshot));

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "qualification cycle byte-capacity session start");
        var ready = await harness.WaitForSnapshotAsync(
            value => value.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "qualification cycle byte-capacity session did not reach readiness");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(41, 7);

        await AssertQualificationCycleAdmissionRollbackAsync(harness, controller, sessionId,
            "qualification cycle byte-capacity admission did not fail closed");
    }

    private static async Task<QualificationCycleHistoryPage> WaitForQualificationCyclePageAsync(
        QualificationHarness harness, Guid sessionId,
        Func<QualificationCycleHistoryPage, bool> predicate, string timeoutReason)
    {
        var query = new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options);
        QualificationCycleHistoryPage? last = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            last = await query.QueryAsync(new(SessionId: sessionId, PageSize: 128));
            if (last.Available && predicate(last)) return last;
            await Task.Delay(25);
        }
        throw new XunitException(timeoutReason + ":" + last?.ReasonCode);
    }

    private static async Task WaitForReadyWriteCountAsync(
        ModbusQualificationTestServer controller, int expected, string timeoutReason)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (controller.ReadyWriteCount >= expected) return;
            await Task.Delay(25);
        }
        throw new XunitException(timeoutReason + ":" + controller.ReadyWriteCount);
    }

    private static async Task AssertQualificationCycleAdmissionRollbackAsync(
        QualificationHarness harness, ModbusQualificationTestServer controller,
        Guid sessionId, string timeoutReason)
    {
        var terminal = await harness.WaitForSnapshotAsync(
            value => value.SessionId == sessionId &&
                (value.Phase == StationQualificationSessionPhase.Closed ||
                    value.Phase == StationQualificationSessionPhase.RecoveryBlocked &&
                    value.ReasonCode == "QualificationModbusRecoveryRequired"),
            timeoutReason);
        Assert.True(terminal.Phase == StationQualificationSessionPhase.Closed ||
            terminal.Phase == StationQualificationSessionPhase.RecoveryBlocked &&
            terminal.ReasonCode == "QualificationModbusRecoveryRequired",
            terminal.ReasonCode);

        var station = await harness.WaitForHistoryAsync(sessionId,
            value => value.Events.Any(eventValue => eventValue.SessionId == sessionId &&
                (eventValue.Phase == StationQualificationSessionPhase.Closed ||
                    eventValue.Phase == StationQualificationSessionPhase.RecoveryBlocked &&
                    eventValue.ReasonCode == "QualificationModbusRecoveryRequired")),
            timeoutReason + " station history");
        Assert.Empty(station.Runs);
        Assert.Equal(0, harness.Facility.StimulusCount);
        Assert.Equal(0, harness.Facility.WriteCount);
        Assert.Equal(0, controller.ResultValidHighCount);
        Assert.Equal(0, controller.ProductionReadyWriteCount);
        Assert.DoesNotContain(controller.Writes, value => value.Function == 0x10 &&
            value.StartAddress != harness.ModbusProfile!.RuntimeStartAddress);

        var live = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(PageSize: 128));
        Assert.True(live.Available, live.ReasonCode);
        Assert.Empty(live.Events);
        Assert.Null(live.PendingEvent);
        Assert.False(live.RecoveryRequired, live.ReasonCode);

        var coldOptions = harness.Fixture.CreateColdSnapshotOptions();
        var coldQuery = new SqliteQualificationCycleHistoryQuery(coldOptions);
        var cold = await coldQuery.QueryAsync(new(PageSize: 128));
        Assert.True(cold.Available, cold.ReasonCode);
        Assert.Empty(cold.Events);
        Assert.Null(cold.PendingEvent);
        Assert.False(cold.RecoveryRequired, cold.ReasonCode);
        var current = await coldQuery.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.False(current.RecoveryRequired, current.ReasonCode);
        Assert.Null(current.LastEvent);

        var coldStation = await new SqliteStationQualificationHistoryQuery(coldOptions)
            .QueryAsync(new StationQualificationHistoryFilter(SessionId: sessionId, PageSize: 128));
        Assert.True(coldStation.Available, coldStation.ReasonCode);
        Assert.Empty(coldStation.Runs);

        var audit = await new SharpInspect.Runtime.Integrity.SqliteAuditIntegrityQuery(coldOptions)
            .VerifyAsync(new AuditVerificationRequest(0,
                coldOptions.AuditIntegrityPolicy!.MaximumVerificationEntries -
                coldOptions.AuditIntegrityPolicy.CheckpointEveryEntries));
        Assert.Equal(AuditIntegrityState.Verified, audit.State);
    }

    private static async Task<string> DescribeQualificationSnapshotAsync(
        QualificationHarness harness)
    {
        try
        {
            var result = await harness.Qualification.GetSnapshotAsync(harness.Invocation());
            if (!result.Available)
                return $"SnapshotUnavailable={result.ReasonCode}";
            var snapshot = result.Snapshot;
            return $"SnapshotPhase={snapshot?.Phase};SnapshotReason={snapshot?.ReasonCode}";
        }
        catch (Exception exception)
        {
            return $"SnapshotReadError={exception.Message}";
        }
    }

    private static async Task<string> DescribeQualificationCycleAsync(
        QualificationHarness harness, Guid sessionId)
    {
        try
        {
            var page = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
                .QueryAsync(new(SessionId: sessionId, PageSize: 128));
            var events = string.Join(",", page.Events.Select(value =>
                $"{value.Position}:{value.Kind}:run={value.RunId?.Value}"));
            return $"CycleAvailable={page.Available};CycleReason={page.ReasonCode};" +
                $"CycleRecoveryRequired={page.RecoveryRequired};CycleEvents=[{events}]";
        }
        catch (Exception exception)
        {
            return $"CycleReadError={exception.Message}";
        }
    }

    private static async Task<long> ReadCycleAuditReserveForTestAsync(
        QualificationFixture fixture)
        => await ReadCycleAuditReserveForTestAsync(fixture.Options);

    private static async Task<long> ReadCycleAuditReserveForTestAsync(
        ProductionStoreOptions options)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return SqliteCommandStore.ReadQualificationCycleAuditReserve(connection.Handle!,
            new StoreDeadline(options.QueryTimeout));
    }

    private static void CycleStorageTamperPolicyHash(string databasePath, long position,
        out string? original)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT PolicySnapshotHash FROM qualification_cycle_events WHERE Position=$position;";
        read.Parameters.AddWithValue("$position", position);
        original = read.ExecuteScalar() as string;
        Assert.False(string.IsNullOrWhiteSpace(original));

        using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TRIGGER qualification_cycle_event_immutable_update;";
        drop.ExecuteNonQuery();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE qualification_cycle_events SET PolicySnapshotHash=$hash WHERE Position=$position;";
        update.Parameters.AddWithValue("$hash", new string('0', 64));
        update.Parameters.AddWithValue("$position", position);
        update.ExecuteNonQuery();
        CycleStorageCreateEventImmutableTrigger(connection);
    }

    private static void CycleStorageRestorePolicyHash(string databasePath, long position,
        string original)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TRIGGER qualification_cycle_event_immutable_update;";
        drop.ExecuteNonQuery();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE qualification_cycle_events SET PolicySnapshotHash=$hash WHERE Position=$position;";
        update.Parameters.AddWithValue("$hash", original);
        update.Parameters.AddWithValue("$position", position);
        update.ExecuteNonQuery();
        CycleStorageCreateEventImmutableTrigger(connection);
    }

    private static void CycleStorageCreateEventImmutableTrigger(SqliteConnection connection)
    {
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TRIGGER qualification_cycle_event_immutable_update " +
            "BEFORE UPDATE ON qualification_cycle_events BEGIN " +
            "SELECT RAISE(ABORT,'ImmutableQualificationCycleEvent'); END;";
        create.ExecuteNonQuery();
    }
}
