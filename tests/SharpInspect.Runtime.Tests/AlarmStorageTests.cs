using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Xunit.Sdk;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

public sealed class AlarmStorageTests
{
    [Fact]
    public async Task V124_A01_HealthySourceBeforeAnyFaultPersistsWithoutCreatingAnAlarmInstance()
    {
        var fixture = CreateFixture("V124-A01");
        try
        {
            await using (var store = new SqliteCommandStore(fixture.Options))
            {
                Assert.True((await store.Initialization).Committed);
                await WaitForVerifiedAsync(store);
                var now = DateTimeOffset.UtcNow;
                var written = await store.UpdateAlarmObservationAsync(fixture.RuntimeEpoch, state =>
                {
                    var decision = AlarmTransitions.Observe(state, new AlarmObservation(fixture.RuntimeEpoch,
                        1, "CAMERA_DISCONNECTED", "Camera", true, now), fixture.RuntimeEpoch, now);
                    Assert.True(decision.Succeeded, decision.ReasonCode);
                    return new AlarmObservationUpdate(decision, decision.Events);
                }, CancellationToken.None);
                Assert.True(written.Committed, written.ReasonCode);
                await WaitForVerifiedAsync(store);
            }
            await using var reopened = new SqliteCommandStore(fixture.Options);
            Assert.True((await reopened.Initialization).Committed);
            await WaitForVerifiedAsync(reopened);
            var history = await new SqliteAlarmHistoryQuery(fixture.Options)
                .QueryAsync(new AlarmHistoryFilter(code: "CAMERA_DISCONNECTED"));
            Assert.True(history.Available, history.ReasonCode);
            var observed = Assert.Single(history.Records);
            Assert.Equal(AlarmTransitionKind.Observed, observed.Transition);
            Assert.Null(observed.Instance);
            Assert.Null(observed.InstanceId);
            Assert.Equal("Camera", observed.Source);
        }
        finally { DeleteMachineKey(fixture.PolicyIntegrity); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task V109_S11_IdentityAndAlarmWritesWaitForTheOtherCommitRecheckWithoutReplayingCallbacks(bool identityFirst)
    {
        if (!OperatingSystem.IsWindows()) throw SkipException.ForSkip("Signed storage requires Windows machine protection.");
        var fixture = CreateFixture("V109-S11");
        try
        {
            await using var store = new SqliteCommandStore(fixture.Options);
            Assert.True((await store.Initialization).Committed);
            await WaitForVerifiedAsync(store);
            await PauseVerifierAsync(store);
            var firstCalls = 0;
            var first = await WriteRaceEventAsync(store, fixture, identityFirst, () => firstCalls++);
            Assert.True(first.Committed, first.ReasonCode);
            Assert.Equal(AuditIntegrityState.Verifying, store.Integrity!.State);

            var secondCalls = 0;
            var pending = WriteRaceEventAsync(store, fixture, !identityFirst, () => secondCalls++).AsTask();
            await Task.Delay(60);
            Assert.False(pending.IsCompleted, "A new write must wait for the previous commit's advisory verification.");
            Assert.Equal(0, secondCalls);
            var verified = await new SqliteAuditIntegrityQuery(fixture.Options)
                .VerifyAsync(new AuditVerificationRequest(0, 100));
            Assert.Equal(AuditIntegrityState.Verified, verified.State);
            SetAdvisoryReport(store, verified);
            var second = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(second.Committed, second.ReasonCode);
            Assert.Equal(1, firstCalls);
            Assert.Equal(1, secondCalls);
            var final = await new SqliteAuditIntegrityQuery(fixture.Options)
                .VerifyAsync(new AuditVerificationRequest(0, 100));
            Assert.Equal(AuditIntegrityState.Verified, final.State);
            Assert.Equal(verified.ThroughSequence + 1, final.ThroughSequence);
            using var connection = Open(fixture.DatabasePath, true);
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM alarm_events WHERE Transition=1;";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
        }
        finally { DeleteMachineKey(fixture.PolicyIntegrity); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task V109_S12_RecheckWaitRetainsOriginalDeadlineAndNeverRetriesFaults(bool identity, bool fault)
    {
        if (!OperatingSystem.IsWindows()) throw SkipException.ForSkip("Signed storage requires Windows machine protection.");
        var fixture = CreateFixture("V109-S12");
        try
        {
            await using var store = new SqliteCommandStore(fixture.Options);
            Assert.True((await store.Initialization).Committed);
            await WaitForVerifiedAsync(store);
            await PauseVerifierAsync(store);
            SetAdvisoryReport(store, SqliteAuditIntegrityQuery.Report(fixture.PolicyIntegrity,
                AuditIntegrityState.Verifying, "AuditRecheckPending"));
            var callbacks = 0;
            var started = System.Diagnostics.Stopwatch.StartNew();
            var pending = WriteRaceEventAsync(store, fixture, identity, () => callbacks++,
                new StoreDeadline(TimeSpan.FromMilliseconds(300))).AsTask();
            await Task.Delay(60);
            Assert.False(pending.IsCompleted);
            SemaphoreSlim? heldQueue = null;
            var heldSlots = 0;
            try
            {
                if (fault)
                {
                    heldQueue = (SemaphoreSlim)typeof(SqliteCommandStore).GetField("_queueSlots",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
                    while (heldQueue.Wait(0)) heldSlots++;
                    Assert.Equal(fixture.Options.QueueCapacity, heldSlots);
                    SetAdvisoryReport(store, SqliteAuditIntegrityQuery.Report(fixture.PolicyIntegrity,
                        AuditIntegrityState.Faulted, "AuditCheckpointInvalid"));
                }
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.False(result.Committed);
                Assert.Equal(0, callbacks);
                Assert.True(started.Elapsed < TimeSpan.FromSeconds(3));
                if (fault)
                {
                    Assert.Equal("AuditCheckpointInvalid", result.ReasonCode);
                    Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(500),
                        "A terminal fault must return without trying to acquire the exhausted queue again.");
                }
                else
                {
                    Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(identity ? 1800 : 250));
                    Assert.Equal(identity ? "IdentityCommitDeadlineExceeded" : "AlarmCommitDeadlineExceeded", result.ReasonCode);
                }
            }
            finally { if (heldSlots > 0) heldQueue!.Release(heldSlots); }
        }
        finally { DeleteMachineKey(fixture.PolicyIntegrity); }
    }

    private static ValueTask<IdentityWriteResult> WriteRaceEventAsync(SqliteCommandStore store, Fixture fixture,
        bool identity, Action observeCallback, StoreDeadline? deadline = null)
    {
        if (!identity)
            return store.UpdateAlarmObservationAsync(fixture.RuntimeEpoch, _ =>
            {
                observeCallback();
                return new AlarmObservationUpdate(null, new[] { NewRaised(fixture, DateTimeOffset.UtcNow, Guid.NewGuid()) });
            }, CancellationToken.None, deadline);
        return store.UpdateIdentityAsync(state =>
        {
            observeCallback();
            var fact = new IdentityAuditEvent(Guid.NewGuid(), IdentityEventKind.SessionLocked,
                DateTimeOffset.UtcNow, state.StationId, Guid.NewGuid(), null, null, null, "PrivacyLocked")
                { SessionId = Guid.NewGuid() };
            return new IdentityUpdate("SessionEvidencePersisted", new[] { fact });
        }, CancellationToken.None);
    }

    private static async Task PauseVerifierAsync(SqliteCommandStore store)
    {
        if (!OperatingSystem.IsWindows()) throw SkipException.ForSkip("Signed storage requires Windows machine protection.");
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        ((CancellationTokenSource)typeof(SqliteCommandStore).GetField("_integrityLifetime", flags)!.GetValue(store)!).Cancel();
        await ((Task)typeof(SqliteCommandStore).GetField("_integrityMonitor", flags)!.GetValue(store)!)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void SetAdvisoryReport(SqliteCommandStore store, AuditIntegrityReport report) =>
        typeof(SqliteCommandStore).GetField("_integrity", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.SetValue(store, report);

    [Fact]
    public async Task V109_S08_MaximumLegalPolicyFitsBoundedStorage()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Alarm storage uses Windows machine protection.");

        var policy = new AlarmPolicy("V109WidePolicy", "v1",
            Enumerable.Range(0, 256).Select(index => new AlarmPolicyRule(
                "C" + index.ToString("D3") + new string('X', 60), new string('S', 64),
                AlarmSeverity.Warning, ProductionImpact.None, false, AlarmNotification.None, null)),
            TimeSpan.FromSeconds(30), maximumActiveInstances: 256, maximumPlcEntries: 1);
        var fixture = CreateFixture("V109-S08", policy);
        try
        {
            await using var store = new SqliteCommandStore(fixture.Options);
            var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitForVerifiedAsync(store);
            var page = await new SqliteAlarmHistoryQuery(fixture.Options).QueryAsync(new AlarmHistoryFilter());
            Assert.True(page.Available, page.ReasonCode);
            Assert.Single(page.Records);
        }
        finally { DeleteMachineKey(fixture.PolicyIntegrity); }
    }

    [Fact]
    public async Task V120_M03_Schema7ReadOnlyAuditWithoutIdentityOrAlarmOptionsAcceptsLargeAlarmPayload()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Legacy schema 7 audit storage requires Windows machine protection.");

        var fixture = CreateFixture("V120-M03", LargeLegacyPolicy());
        try
        {
            await using (var store = new SqliteCommandStore(fixture.Options))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);
            }

            byte[] payload;
            using (var connection = Open(fixture.DatabasePath, readOnly: true))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version;";
                Assert.Equal(7L, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
                command.CommandText = "SELECT Payload FROM alarm_events WHERE Position=1;";
                var encoded = Assert.IsType<string>(command.ExecuteScalar());
                payload = Convert.FromBase64String(encoded);
            }

            Assert.InRange(payload.Length, 64 * 1024 + 1, 131_072);
            var before = SHA256.HashData(File.ReadAllBytes(fixture.DatabasePath));
            var readOnlyOptions = new ProductionStoreOptions(fixture.DatabasePath)
            {
                AuditIntegrityPolicy = fixture.Options.AuditIntegrityPolicy,
                CommitTimeout = fixture.Options.CommitTimeout,
                QueryTimeout = fixture.Options.QueryTimeout,
                QueueCapacity = fixture.Options.QueueCapacity
            };
            Assert.Null(readOnlyOptions.LocalIdentity);
            Assert.Null(readOnlyOptions.AlarmPolicy);

            var report = await new SqliteAuditIntegrityQuery(readOnlyOptions)
                .VerifyAsync(new AuditVerificationRequest());

            Assert.Equal(AuditIntegrityState.Verified, report.State);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.DatabasePath)));
        }
        finally { DeleteMachineKey(fixture.PolicyIntegrity); }
    }

    private static AlarmPolicy LargeLegacyPolicy() => new("V120LegacyAlarmPolicy", "1",
        Enumerable.Range(0, 256).Select(index => new AlarmPolicyRule(
            "C" + index.ToString("D3", CultureInfo.InvariantCulture) + new string('X', 60),
            new string('S', 64), AlarmSeverity.Critical, ProductionImpact.FaultAbort, true,
            AlarmNotification.UntilCleared, ushort.MaxValue, ushort.MaxValue,
            AlarmResetPrerequisites.NoActiveExecution | AlarmResetPrerequisites.NoPendingDelivery |
            AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoExclusiveMode)),
        TimeSpan.FromMinutes(1), maximumActiveInstances: 256, maximumPlcEntries: 16);

    [Fact]
    public async Task V109_S09_TamperedCodeCannotDisappearFromFilteredHistory()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Alarm storage uses Windows machine protection.");

        var fixture = CreateFixture("V109-S09");
        try
        {
            await using (var store = new SqliteCommandStore(fixture.Options))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);
                var result = await store.UpdateAlarmObservationAsync(fixture.RuntimeEpoch,
                    _ => new AlarmObservationUpdate(null, new[]
                    {
                        NewRaised(fixture, DateTimeOffset.UtcNow, Guid.NewGuid()),
                        NewRaised(fixture, DateTimeOffset.UtcNow, Guid.NewGuid()),
                        NewRaised(fixture, DateTimeOffset.UtcNow, Guid.NewGuid())
                    }), CancellationToken.None);
                Assert.True(result.Committed, result.ReasonCode);
                await WaitForVerifiedAsync(store);
            }

            using (var connection = Open(fixture.DatabasePath, false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DROP TRIGGER alarm_events_immutable_update; UPDATE alarm_events SET Code='TAMPERED_CODE' WHERE Position=2; " +
                    "CREATE TRIGGER alarm_events_immutable_update BEFORE UPDATE ON alarm_events BEGIN SELECT RAISE(ABORT,'ImmutableAlarmEvent'); END;";
                command.ExecuteNonQuery();
            }

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await new SqliteAlarmHistoryQuery(fixture.Options).QueryAsync(
                    new AlarmHistoryFilter(code: "CAMERA_DISCONNECTED")));
        }
        finally { DeleteMachineKey(fixture.PolicyIntegrity); }
    }

    [Fact]
    public async Task V109_S10_TamperedOldPayloadAndRecomputedRowHashCannotPassFullVerification()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Alarm storage uses Windows machine protection.");

        var fixture = CreateFixture("V109-S10");
        try
        {
            await using (var store = new SqliteCommandStore(fixture.Options))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);
                var result = await store.UpdateAlarmObservationAsync(fixture.RuntimeEpoch,
                    _ => new AlarmObservationUpdate(null, new[]
                    {
                        NewRaised(fixture, DateTimeOffset.UtcNow, Guid.NewGuid()),
                        NewRaised(fixture, DateTimeOffset.UtcNow, Guid.NewGuid()),
                        NewRaised(fixture, DateTimeOffset.UtcNow, Guid.NewGuid())
                    }), CancellationToken.None);
                Assert.True(result.Committed, result.ReasonCode);
                await WaitForVerifiedAsync(store);
            }

            using (var connection = Open(fixture.DatabasePath, false))
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT Payload FROM alarm_events WHERE Position=2;";
                var original = (string)read.ExecuteScalar()!;
                read.CommandText = "SELECT (SELECT Sequence FROM audit_entries WHERE AlarmPosition=2) < " +
                    "(SELECT MAX(Sequence) FROM audit_checkpoints);";
                Assert.Equal(1L, Convert.ToInt64(read.ExecuteScalar()));
                var tampered = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    Encoding.UTF8.GetString(Convert.FromBase64String(original)).Replace(
                        "CAMERA_DISCONNECTED", "TAMPERED_CODE", StringComparison.Ordinal)));
                var hash = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(tampered)));
                using var update = connection.CreateCommand();
                update.CommandText = "DROP TRIGGER alarm_events_immutable_update; UPDATE alarm_events " +
                    "SET Code='TAMPERED_CODE', Payload=$payload, PayloadHash=$hash WHERE Position=2; " +
                    "CREATE TRIGGER alarm_events_immutable_update BEFORE UPDATE ON alarm_events BEGIN SELECT RAISE(ABORT,'ImmutableAlarmEvent'); END; " +
                    "DROP TRIGGER audit_entries_immutable_update; UPDATE audit_entries SET Payload=$payload WHERE AlarmPosition=2; " +
                    "CREATE TRIGGER audit_entries_immutable_update BEFORE UPDATE ON audit_entries BEGIN SELECT RAISE(ABORT, 'ImmutableAuditEvidence'); END;";
                update.Parameters.AddWithValue("$payload", tampered);
                update.Parameters.AddWithValue("$hash", hash);
                update.ExecuteNonQuery();
            }

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await new SqliteAlarmHistoryQuery(fixture.Options).QueryAsync(new AlarmHistoryFilter()));
        }
        finally { DeleteMachineKey(fixture.PolicyIntegrity); }
    }

    [Fact]
    public async Task V109_S01_PolicyAndActivationHistorySurviveReopen()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Alarm storage uses Windows machine protection.");

        var fixture = CreateFixture("V109-S01");
        try
        {
            await using (var store = new SqliteCommandStore(fixture.Options))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);

                var page = await new SqliteAlarmHistoryQuery(fixture.Options).QueryAsync(new AlarmHistoryFilter());
                Assert.True(page.Available, page.ReasonCode);
                var activation = Assert.Single(page.Records);
                Assert.Equal(AlarmTransitionKind.PolicyActivated, activation.Transition);
                Assert.Equal(fixture.Policy.ContentHash, fixture.Options.AlarmPolicy!.ContentHash);

                var state = await store.ReadAlarmStateAsync(fixture.RuntimeEpoch);
                Assert.True(state.Available, state.ReasonCode);
                Assert.Equal(fixture.Policy.ContentHash, state.Policy!.ContentHash);
                Assert.Empty(state.Instances);
            }

            await using (var reopened = new SqliteCommandStore(fixture.Options))
            {
                var initialized = await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(reopened);
                var page = await new SqliteAlarmHistoryQuery(fixture.Options).QueryAsync(new AlarmHistoryFilter());
                Assert.Single(page.Records);
                Assert.Equal(1L, page.ThroughPosition);
            }
        }
        finally { DeleteMachineKey(fixture.PolicyIntegrity); }
    }

    [Fact]
    public async Task V109_S02_ObservationAllocatesSignedPositionAndPaginates()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Alarm storage uses Windows machine protection.");

        var fixture = CreateFixture("V109-S02");
        try
        {
            await using var store = new SqliteCommandStore(fixture.Options);
            var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitForVerifiedAsync(store);
            var now = DateTimeOffset.UtcNow;
            var instanceId = Guid.NewGuid();
            var instance = new AlarmInstanceSnapshot(instanceId, "CAMERA_DISCONNECTED", "Camera",
                fixture.Policy.Id, fixture.Policy.Version, fixture.Policy.ContentHash, AlarmSeverity.Error,
                ProductionImpact.FaultAbort, true, AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete, now, now, false, fixture.RuntimeEpoch, 1,
                AlarmLifecycle.Active, false, null, null, 0);

            var result = await store.UpdateAlarmObservationAsync(fixture.RuntimeEpoch, _ =>
                new AlarmObservationUpdate(new object(), new[]
                {
                    new AlarmHistoryRecord(0, Guid.NewGuid(), instanceId, instance.Code, instance.Source,
                        AlarmTransitionKind.Raised, now, now, null, null, null, "AlarmRaised", instance, null)
                }), CancellationToken.None);
            Assert.True(result.Committed, result.ReasonCode);
            await WaitForVerifiedAsync(store);

            var more = await store.UpdateAlarmObservationAsync(fixture.RuntimeEpoch, _ =>
                new AlarmObservationUpdate(null, new[]
                {
                    NewRaised(fixture, now, Guid.NewGuid()),
                    NewRaised(fixture, now, Guid.NewGuid())
                }), CancellationToken.None);
            Assert.True(more.Committed, more.ReasonCode);
            await WaitForVerifiedAsync(store);

            var page = await new SqliteAlarmHistoryQuery(fixture.Options).QueryAsync(new AlarmHistoryFilter(pageSize: 1));
            Assert.Equal(4L, page.ThroughPosition);
            Assert.Single(page.Records);
            Assert.NotNull(page.NextAfterPosition);
            var second = await new SqliteAlarmHistoryQuery(fixture.Options).QueryAsync(
                new AlarmHistoryFilter(afterPosition: page.NextAfterPosition!.Value, pageSize: 1));
            var raised = Assert.Single(second.Records);
            Assert.Equal(2L, raised.Position);
            Assert.Equal(2L, raised.Instance!.LastTransitionPosition);
            Assert.NotNull(second.NextAfterPosition);
            var third = await new SqliteAlarmHistoryQuery(fixture.Options).QueryAsync(
                new AlarmHistoryFilter(afterPosition: second.NextAfterPosition!.Value, pageSize: 1));
            Assert.Equal(3L, Assert.Single(third.Records).Position);
            Assert.NotNull(third.NextAfterPosition);
            var fourth = await new SqliteAlarmHistoryQuery(fixture.Options).QueryAsync(
                new AlarmHistoryFilter(afterPosition: third.NextAfterPosition!.Value, pageSize: 1));
            Assert.Equal(4L, Assert.Single(fourth.Records).Position);

            var state = await store.ReadAlarmStateAsync(fixture.RuntimeEpoch);
            Assert.Equal(3, state.Instances.Count);
            Assert.Contains(state.Instances, current => current.LastTransitionPosition == 2L);
            Assert.True(state.Plc.TotalUncleared >= 3);
        }
        finally { DeleteMachineKey(fixture.PolicyIntegrity); }
    }

    [Fact]
    public async Task V109_S03_EmptyObservationIsCommittedNoChangeWithoutAuditEvent()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Alarm storage uses Windows machine protection.");

        var fixture = CreateFixture("V109-S03");
        try
        {
            await using var store = new SqliteCommandStore(fixture.Options);
            var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitForVerifiedAsync(store);
            var result = await store.UpdateAlarmObservationAsync(fixture.RuntimeEpoch,
                _ => new AlarmObservationUpdate("unchanged", Array.Empty<AlarmHistoryRecord>()), CancellationToken.None);
            Assert.True(result.Committed, result.ReasonCode);
            Assert.Equal("unchanged", result.Result);
            var page = await new SqliteAlarmHistoryQuery(fixture.Options).QueryAsync(new AlarmHistoryFilter());
            Assert.Single(page.Records);
            Assert.Equal(1L, page.ThroughPosition);
        }
        finally { DeleteMachineKey(fixture.PolicyIntegrity); }
    }

    [Fact]
    public async Task V109_S04_LaterEventFailureRollsBackEarlierAlarmInsert()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Alarm storage uses Windows machine protection.");

        var fixture = CreateFixture("V109-S04");
        try
        {
            await using var store = new SqliteCommandStore(fixture.Options);
            var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitForVerifiedAsync(store);
            var now = DateTimeOffset.UtcNow;
            var first = NewRaised(fixture, now, Guid.NewGuid());
            var duplicateId = Guid.NewGuid();
            var second = NewRaised(fixture, now, duplicateId);
            var duplicate = second with { EventId = first.EventId };
            var result = await store.UpdateAlarmObservationAsync(fixture.RuntimeEpoch,
                _ => new AlarmObservationUpdate(null, new[] { first, second, duplicate }), CancellationToken.None);
            Assert.False(result.Committed, result.ReasonCode);
            Assert.Contains("Alarm", result.ReasonCode, StringComparison.Ordinal);

            var page = await new SqliteAlarmHistoryQuery(fixture.Options).QueryAsync(new AlarmHistoryFilter());
            Assert.Single(page.Records);
            Assert.Equal(1L, page.ThroughPosition);
        }
        finally { DeleteMachineKey(fixture.PolicyIntegrity); }
    }

    [Fact]
    public async Task V109_S05_DeletingAlarmHistoryCannotReopenStore()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Alarm storage uses Windows machine protection.");

        var fixture = CreateFixture("V109-S05");
        try
        {
            await using (var store = new SqliteCommandStore(fixture.Options))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);
                var now = DateTimeOffset.UtcNow;
                var result = await store.UpdateAlarmObservationAsync(fixture.RuntimeEpoch,
                    _ => new AlarmObservationUpdate(null, new[] { NewRaised(fixture, now, Guid.NewGuid()) }), CancellationToken.None);
                Assert.True(result.Committed, result.ReasonCode);
                await WaitForVerifiedAsync(store);
            }

            using (var connection = Open(fixture.DatabasePath, false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DROP TRIGGER alarm_events_immutable_delete; DELETE FROM alarm_events WHERE Position=2; " +
                    "CREATE TRIGGER alarm_events_immutable_delete BEFORE DELETE ON alarm_events BEGIN SELECT RAISE(ABORT,'ImmutableAlarmEvent'); END;";
                command.ExecuteNonQuery();
            }

            await using var rejected = new SqliteCommandStore(fixture.Options);
            var rejectedResult = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(rejectedResult.Committed);
            Assert.Equal("AuditUnchainedFact", rejectedResult.ReasonCode);
        }
        finally { DeleteMachineKey(fixture.PolicyIntegrity); }
    }

    [Fact]
    public async Task V109_S06_TamperedPolicyDocumentCannotPassSignedActivationBinding()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Alarm storage uses Windows machine protection.");

        var fixture = CreateFixture("V109-S06");
        try
        {
            await using (var store = new SqliteCommandStore(fixture.Options))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);
            }

            using (var connection = Open(fixture.DatabasePath, false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DROP TRIGGER alarm_policy_immutable_update; UPDATE alarm_policy_versions SET CanonicalJson=replace(CanonicalJson,'V109AlarmPolicy','TamperedPolicy'); " +
                    "CREATE TRIGGER alarm_policy_immutable_update BEFORE UPDATE ON alarm_policy_versions BEGIN SELECT RAISE(ABORT,'ImmutableAlarmPolicy'); END;";
                command.ExecuteNonQuery();
            }

            await using var reopened = new SqliteCommandStore(fixture.Options);
            var initializedAgain = await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(initializedAgain.Committed);
            Assert.Equal("AlarmPolicyHashMismatch", initializedAgain.ReasonCode);
        }
        finally { DeleteMachineKey(fixture.PolicyIntegrity); }
    }

    [Fact]
    public async Task V109_S07_TamperedRuntimeEpochCannotPassAlarmPayloadBinding()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Alarm storage uses Windows machine protection.");

        var fixture = CreateFixture("V109-S07");
        try
        {
            await using (var store = new SqliteCommandStore(fixture.Options))
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);
                var result = await store.UpdateAlarmObservationAsync(fixture.RuntimeEpoch,
                    _ => new AlarmObservationUpdate(null, new[] { NewRaised(fixture, DateTimeOffset.UtcNow, Guid.NewGuid()) }), CancellationToken.None);
                Assert.True(result.Committed, result.ReasonCode);
                await WaitForVerifiedAsync(store);
            }

            using (var connection = Open(fixture.DatabasePath, false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DROP TRIGGER alarm_events_immutable_update; UPDATE alarm_events SET RuntimeEpoch=$epoch WHERE Position=2; " +
                    "CREATE TRIGGER alarm_events_immutable_update BEFORE UPDATE ON alarm_events BEGIN SELECT RAISE(ABORT,'ImmutableAlarmEvent'); END;";
                command.Parameters.AddWithValue("$epoch", Guid.NewGuid().ToString("D"));
                command.ExecuteNonQuery();
            }

            await using var reopened = new SqliteCommandStore(fixture.Options);
            var initializedAgain = await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(initializedAgain.Committed);
            Assert.Equal("AlarmHistoryBindingMismatch", initializedAgain.ReasonCode);
        }
        finally { DeleteMachineKey(fixture.PolicyIntegrity); }
    }

    private static AlarmHistoryRecord NewRaised(Fixture fixture, DateTimeOffset now, Guid instanceId)
    {
        var instance = new AlarmInstanceSnapshot(instanceId, "CAMERA_DISCONNECTED", "Camera",
            fixture.Policy.Id, fixture.Policy.Version, fixture.Policy.ContentHash, AlarmSeverity.Error,
            ProductionImpact.FaultAbort, true, AlarmNotification.UntilCleared, null, 0,
            AlarmResetPrerequisites.RecoveryComplete, now, now, false, fixture.RuntimeEpoch, 1,
            AlarmLifecycle.Active, false, null, null, 0);
        return new AlarmHistoryRecord(0, Guid.NewGuid(), instanceId, instance.Code, instance.Source,
            AlarmTransitionKind.Raised, now, now, null, null, null, "AlarmRaised", instance, null);
    }

    private static Fixture CreateFixture(string name, AlarmPolicy? configuredPolicy = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "alarms.sqlite");
        var integrity = new AuditIntegrityPolicy("V109AlarmStation", "v1", "SharpInspect.Test.V109." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true, KeyDirectory = Path.Combine(directory, "keys"),
            CheckpointEveryEntries = 2, VerificationInterval = TimeSpan.FromSeconds(1)
        };
        var identity = new LocalIdentityOptions("V109AlarmStation",
            new LocalPasswordPolicy { Blocklist = PasswordBlocklist.Create("v109-blocklist", "v1", new[] { "known-compromised" }) },
            new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, AuthorizationPolicy.Development);
        var alarm = configuredPolicy ?? new AlarmPolicy("V109AlarmPolicy", "v1", new[]
        {
            new AlarmPolicyRule("CAMERA_DISCONNECTED", "Camera", AlarmSeverity.Error,
                ProductionImpact.FaultAbort, true, AlarmNotification.UntilCleared, null)
        }, TimeSpan.FromSeconds(30));
        var options = new ProductionStoreOptions(databasePath)
        {
            AuditIntegrityPolicy = integrity, LocalIdentity = identity, AlarmPolicy = alarm,
            CommitTimeout = TimeSpan.FromSeconds(2), QueryTimeout = TimeSpan.FromSeconds(2), QueueCapacity = 8
        };
        return new Fixture(options, integrity, databasePath, alarm, Guid.NewGuid());
    }

    private static async Task WaitForVerifiedAsync(SqliteCommandStore store)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (store.Integrity is { State: AuditIntegrityState.Verified }) return;
            if (store.Integrity is { State: AuditIntegrityState.Faulted } fault)
                throw new XunitException(fault.ReasonCode);
            await Task.Delay(25);
        }
        throw new XunitException($"Audit integrity did not become Verified: {store.Integrity?.ReasonCode}");
    }

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void DeleteMachineKey(AuditIntegrityPolicy policy)
    {
        try
        {
            var keyPath = WindowsMachineAuditKey.GetKeyPath(policy);
            if (File.Exists(keyPath)) File.Delete(keyPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record Fixture(ProductionStoreOptions Options, AuditIntegrityPolicy PolicyIntegrity,
        string DatabasePath, AlarmPolicy Policy, Guid RuntimeEpoch);
}

#pragma warning restore CA1416
