using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.PartIdentity;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V143_S09_IndependentRejectRechecksCentralChainWithoutAdvancing()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await CreateRejectedPartIdentityHarnessAsync(peer);

        await RaiseRejectedTriggerAsync(harness, peer, 1, 2, null);
        await harness.StopRuntimePreservingFixtureAsync();
        await PauseIntegrityMonitorAsync(harness.Fixture.Store);

        var before = await ReadPartIdentityAuditCountsAsync(harness.Fixture.Options.DatabasePath);
        Assert.True(before.PartIdentityRows > 0);
        Assert.True(before.PartIdentityAuditSequence > 0);
        await TamperCentralPartIdentityAuditHashAsync(harness.Fixture.Options.DatabasePath,
            before.PartIdentityAuditSequence);

        var station = harness.Fixture.Options.LocalIdentity!.StationId;
        var endpoint = harness.Service<ProductionInspectionOptions>().Profile.EndpointBindingHash;
        var rejected = new PartIdentityHistoryEvent(
            position: 1, previousHash: null, eventId: Guid.NewGuid(),
            kind: PartIdentityHistoryEventKind.RejectedTrigger, correlationId: Guid.NewGuid(),
            attemptId: Guid.NewGuid(), runtimeEpoch: Guid.NewGuid(), stationId: station,
            controllerEpoch: 61, cycleSequence: 99, endpointBindingHash: endpoint,
            evidence: null, reasonCode: "PartIdentityRequiredMissing",
            recordedAtUtc: DateTimeOffset.UtcNow,
            rejectionContext: new PartIdentityRejectionContext(requirement: null,
                connectionGeneration: 1, rawProofBytes: new byte[] { 0x09 },
                readEvidenceHash: new string('B', 64)));

        var result = await harness.Fixture.Store.AppendPartIdentityEventAsync(
            new PartIdentityWriteRequest(rejected), new StoreDeadline(TimeSpan.FromSeconds(5)));

        Assert.False(result.Committed, result.ReasonCode);
        Assert.Equal("AuditPayloadHashMismatch", result.ReasonCode);
        var after = await ReadPartIdentityAuditCountsAsync(harness.Fixture.Options.DatabasePath);
        Assert.Equal(before.PartIdentityRows, after.PartIdentityRows);
        Assert.Equal(before.AuditRows, after.AuditRows);
        Assert.Equal(before.AuditTailSequence, after.AuditTailSequence);
        Assert.Equal(before.PartIdentityAuditSequence, after.PartIdentityAuditSequence);
    }

    [Fact]
    public async Task V143_S03_RejectedTriggerIsColdReadableWithoutProductionInspection()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await CreateRejectedPartIdentityHarnessAsync(peer);

        await RaiseRejectedTriggerAsync(harness, peer, 1, 2, null);
        await harness.StopRuntimePreservingFixtureAsync();
        await harness.Fixture.Store.DisposeAsync();

        var identity = await new SqlitePartIdentityHistoryQuery(harness.Fixture.Options)
            .ReadCurrentAsync();
        Assert.True(identity.Available, identity.ReasonCode);
        Assert.Equal(PartIdentityHistoryEventKind.RejectedTrigger, identity.Latest!.Kind);
        Assert.Null(identity.Latest.InspectionId);
        Assert.Equal((uint)1, identity.Latest.CycleSequence);
        Assert.Equal("PartIdentityRequiredMissing", identity.Latest.ReasonCode);

        var production = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(PageSize: 128));
        Assert.True(production.Available, production.ReasonCode);
        Assert.Empty(production.Events);
    }

    [Fact]
    public async Task V143_S04_RejectedTriggerHistorySupportsFrozenPaginationAndFilters()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await CreateRejectedPartIdentityHarnessAsync(peer);

        await RaiseRejectedTriggerAsync(harness, peer, 1, 2, null);
        await RaiseRejectedTriggerAsync(harness, peer, 2, 3, null);
        await harness.StopRuntimePreservingFixtureAsync();
        await harness.Fixture.Store.DisposeAsync();

        var query = new SqlitePartIdentityHistoryQuery(harness.Fixture.Options);
        var first = await query.QueryAsync(new(PageSize: 1));
        Assert.True(first.Available, first.ReasonCode);
        var firstEvent = Assert.Single(first.Events);
        Assert.Equal((uint)1, firstEvent.CycleSequence);
        Assert.NotNull(first.NextAfterPosition);
        Assert.Equal(2, first.ThroughPosition);

        var second = await query.QueryAsync(new(AfterPosition: first.NextAfterPosition!.Value,
            ThroughPosition: first.ThroughPosition, PageSize: 1));
        Assert.True(second.Available, second.ReasonCode);
        var secondEvent = Assert.Single(second.Events);
        Assert.Equal((uint)2, secondEvent.CycleSequence);
        Assert.Null(second.NextAfterPosition);
        Assert.Equal(first.ThroughPosition, second.ThroughPosition);

        var runtimePage = await query.QueryAsync(new(
            RuntimeEpoch: firstEvent.RuntimeEpoch, PageSize: 128));
        Assert.True(runtimePage.Available, runtimePage.ReasonCode);
        Assert.Equal(2, runtimePage.Events.Count);
        Assert.All(runtimePage.Events, value =>
            Assert.Equal(PartIdentityHistoryEventKind.RejectedTrigger, value.Kind));
    }

    [Fact]
    public async Task V143_S05_TamperedReasonProjectionIsRejectedAndOriginalBackupRestoresRead()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await CreateRejectedPartIdentityHarnessAsync(peer);
        await RaiseRejectedTriggerAsync(harness, peer, 1, 2, null);

        await harness.StopRuntimePreservingFixtureAsync();
        await harness.Fixture.Store.DisposeAsync();
        var databasePath = harness.Fixture.Options.DatabasePath;
        var backupPath = Path.Combine(Path.GetTempPath(),
            "SharpInspect-V143-S05-" + Guid.NewGuid().ToString("N") + ".sqlite");
        await BackupPartIdentityDatabaseAsync(databasePath, backupPath);
        try
        {
            await TamperPartIdentityProjectionAsync(databasePath, "ReasonCode",
                "PartIdentityProjectionTampered");

            var rejected = await new SqlitePartIdentityHistoryQuery(harness.Fixture.Options)
                .ReadCurrentAsync();
            Assert.False(rejected.Available);
            Assert.Contains("PartIdentity", rejected.ReasonCode, StringComparison.Ordinal);
            Assert.True(File.Exists(backupPath));
            Assert.True(new FileInfo(backupPath).Length > 0);

            DeleteSqliteSidecars(databasePath);
            File.Copy(backupPath, databasePath, overwrite: true);
            var restored = await new SqlitePartIdentityHistoryQuery(harness.Fixture.Options)
                .ReadCurrentAsync();
            Assert.True(restored.Available, restored.ReasonCode);
            Assert.Equal(PartIdentityHistoryEventKind.RejectedTrigger, restored.Latest!.Kind);
            Assert.Equal("PartIdentityRequiredMissing", restored.Latest.ReasonCode);
        }
        finally
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
            DeleteSqliteSidecars(databasePath);
        }
    }

    [Fact]
    public async Task V143_S06_TamperedRuntimeIdentityColumnFailsReadOnlyVerification()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await CreateRejectedPartIdentityHarnessAsync(peer);
        await RaiseRejectedTriggerAsync(harness, peer, 1, 2, null);

        await harness.StopRuntimePreservingFixtureAsync();
        await harness.Fixture.Store.DisposeAsync();
        var databasePath = harness.Fixture.Options.DatabasePath;
        var backupPath = Path.Combine(Path.GetTempPath(),
            "SharpInspect-V143-S06-" + Guid.NewGuid().ToString("N") + ".sqlite");
        await BackupPartIdentityDatabaseAsync(databasePath, backupPath);
        try
        {
            await TamperPartIdentityProjectionAsync(databasePath, "RuntimeEpoch",
                Guid.NewGuid().ToString("D"));

            var rejected = await new SqlitePartIdentityHistoryQuery(harness.Fixture.Options)
                .ReadCurrentAsync();
            Assert.False(rejected.Available);
            Assert.Contains("PartIdentity", rejected.ReasonCode, StringComparison.Ordinal);

            var audit = await new SqliteAuditIntegrityQuery(harness.Fixture.Options)
                .VerifyAsync(new AuditVerificationRequest(0, 200));
            Assert.Equal(AuditIntegrityState.Faulted, audit.State);

            DeleteSqliteSidecars(databasePath);
            File.Copy(backupPath, databasePath, overwrite: true);
            var restored = await new SqlitePartIdentityHistoryQuery(harness.Fixture.Options)
                .ReadCurrentAsync();
            Assert.True(restored.Available, restored.ReasonCode);
            Assert.Equal((uint)1, restored.Latest!.CycleSequence);
        }
        finally
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
            DeleteSqliteSidecars(databasePath);
        }
    }

    [Fact]
    public async Task V143_S07_CapacityFailureDoesNotPartiallyAppendRejectedTrigger()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await CreateRejectedPartIdentityHarnessAsync(peer,
            new PartIdentityStoreOptions
            {
                MaximumEntries = 1,
                MaximumPayloadBytes = 64 * 1024,
                MaximumTotalBytes = 128 * 1024
            });

        await RaiseRejectedTriggerAsync(harness, peer, 1, 2, null);
        var beforeRows = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM part_identity_events;");
        var beforeCentralRows = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM audit_entries WHERE PartIdentityPosition IS NOT NULL;");
        Assert.Equal(1, beforeRows);
        Assert.Equal(1, beforeCentralRows);

        peer.SetPartIdentity(4, 61, 2, null, 3);
        peer.RaiseTrigger(61, 2);
        await WaitProductionAsync(harness,
            state => state.ArmState == ProductionArmState.Disarmed,
            "Part identity capacity failure disarms the production owner");

        Assert.Equal(beforeRows, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM part_identity_events;"));
        Assert.Equal(beforeCentralRows, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM audit_entries WHERE PartIdentityPosition IS NOT NULL;"));
        var page = await new SqlitePartIdentityHistoryQuery(harness.Fixture.Options)
            .ReadCurrentAsync();
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal((uint)1, page.Latest!.CycleSequence);
        Assert.Equal("PartIdentityRequiredMissing", page.Latest.ReasonCode);
    }

    [Fact]
    public async Task V143_S08_CapacityFailureLeavesExistingHistoryAndAuditColdReadable()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await CreateRejectedPartIdentityHarnessAsync(peer,
            new PartIdentityStoreOptions
            {
                MaximumEntries = 1,
                MaximumPayloadBytes = 64 * 1024,
                MaximumTotalBytes = 128 * 1024
            });

        await RaiseRejectedTriggerAsync(harness, peer, 1, 2, null);
        peer.SetPartIdentity(4, 61, 2, null, 3);
        peer.RaiseTrigger(61, 2);
        await WaitProductionAsync(harness,
            state => state.ArmState == ProductionArmState.Disarmed,
            "Part identity capacity failure before cold verification");

        await harness.StopRuntimePreservingFixtureAsync();
        await harness.Fixture.Store.DisposeAsync();

        var query = new SqlitePartIdentityHistoryQuery(harness.Fixture.Options);
        var page = await query.QueryAsync(new(ThroughPosition: 1, PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Single(page.Events);
        Assert.Equal((uint)1, page.Events[0].CycleSequence);
        Assert.Equal(1, page.ThroughPosition);
        Assert.Null(page.NextAfterPosition);

        var audit = await new SqliteAuditIntegrityQuery(harness.Fixture.Options)
            .VerifyAsync(new AuditVerificationRequest(0, 200));
        Assert.Equal(AuditIntegrityState.Verified, audit.State);
        var production = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(PageSize: 128));
        Assert.True(production.Available, production.ReasonCode);
        Assert.Empty(production.Events);
    }

    private static async Task<ManualHarness> CreateRejectedPartIdentityHarnessAsync(
        ModbusQualificationTestServer peer, PartIdentityStoreOptions? storeOptions = null)
    {
        peer.HoldFirstPayloadWrite = false;
        var plan = new ModbusPartIdentityReadPlan("V143.S.Part", "1", 600, 64);
        var binding = RuntimeIdentityBinding(PartIdentityProviderSourceKind.StablePlc,
            plan.ContentHash);
        var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer,
            productionPartRequirement: RuntimeIdentityRequirement(
                PartIdentityRequirementMode.Required, binding),
            partIdentityReadPlan: plan,
            partIdentityStore: storeOptions ?? new PartIdentityStoreOptions(),
            configureAdditionalServices: services => services.AddSingleton<IPartIdentityProvider>(
                new ModbusPartIdentityProvider(binding, plan)));
        try
        {
            using var issuer = new ProductionTestIssuer();
            await PrepareProductionAsync(harness, issuer);
            await ArmProductionAsync(harness);
            await WaitProductionAsync(harness, state => state.Ready,
                "Part identity audit storage production ready");
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    private static async Task<PartIdentityHistoryEvent> RaiseRejectedTriggerAsync(
        ManualHarness harness, ModbusQualificationTestServer peer, uint cycle,
        ushort sourceState, string? value)
    {
        peer.SetPartIdentity(checked(cycle * 2), 61, cycle, value, sourceState);
        peer.RaiseTrigger(61, cycle);
        var query = new SqlitePartIdentityHistoryQuery(harness.Fixture.Options);
        await WaitIdentityConditionAsync(async () =>
        {
            var current = await query.ReadCurrentAsync();
            return current.Latest is { Kind: PartIdentityHistoryEventKind.RejectedTrigger } latest &&
                latest.CycleSequence == cycle;
        }, "Part identity rejection was not durably appended");
        return (await query.ReadCurrentAsync()).Latest!;
    }

    private static async Task TamperPartIdentityProjectionAsync(string databasePath,
        string column, string value)
    {
        if (column is not ("ReasonCode" or "RuntimeEpoch"))
            throw new ArgumentOutOfRangeException(nameof(column));
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await ExecuteSqlAsync(connection, "BEGIN IMMEDIATE;");
        try
        {
            await ExecuteSqlAsync(connection, "DROP TRIGGER part_identity_event_immutable_update;");
            await ExecuteSqlAsync(connection, "DROP TRIGGER part_identity_event_immutable_delete;");
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"UPDATE part_identity_events SET {column}=$value WHERE Position=1;";
                command.Parameters.AddWithValue("$value", value);
                await command.ExecuteNonQueryAsync();
            }
            await ExecuteSqlAsync(connection, @"
                CREATE TRIGGER part_identity_event_immutable_update
                BEFORE UPDATE ON part_identity_events BEGIN
                    SELECT RAISE(ABORT,'ImmutablePartIdentityEvent'); END;");
            await ExecuteSqlAsync(connection, @"
                CREATE TRIGGER part_identity_event_immutable_delete
                BEFORE DELETE ON part_identity_events BEGIN
                    SELECT RAISE(ABORT,'ImmutablePartIdentityEvent'); END;");
            await ExecuteSqlAsync(connection, "COMMIT;");
        }
        catch
        {
            try { await ExecuteSqlAsync(connection, "ROLLBACK;"); }
            catch (SqliteException) { }
            throw;
        }
    }

    private static async Task PauseIntegrityMonitorAsync(SqliteCommandStore store)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var lifetime = (CancellationTokenSource)typeof(SqliteCommandStore)
            .GetField("_integrityLifetime", flags)!.GetValue(store)!;
        lifetime.Cancel();
        var monitor = (Task?)typeof(SqliteCommandStore)
            .GetField("_integrityMonitor", flags)!.GetValue(store);
        if (monitor is not null)
            await monitor.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<PartIdentityAuditCounts> ReadPartIdentityAuditCountsAsync(
        string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT
                (SELECT COUNT(*) FROM part_identity_events),
                (SELECT COUNT(*) FROM audit_entries),
                COALESCE((SELECT MAX(Sequence) FROM audit_entries),0),
                COALESCE((SELECT MAX(Sequence) FROM audit_entries
                    WHERE PartIdentityPosition IS NOT NULL),0);";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new PartIdentityAuditCounts(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private static async Task TamperCentralPartIdentityAuditHashAsync(
        string databasePath, long sequence)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await ExecuteSqlAsync(connection, "BEGIN IMMEDIATE;");
        try
        {
            string? trigger;
            await using (var lookup = connection.CreateCommand())
            {
                lookup.CommandText = @"
                    SELECT sql FROM sqlite_master
                    WHERE type='trigger' AND name='audit_entries_immutable_update';";
                trigger = (string?)await lookup.ExecuteScalarAsync();
            }
            Assert.False(string.IsNullOrWhiteSpace(trigger));
            await ExecuteSqlAsync(connection, "DROP TRIGGER audit_entries_immutable_update;");
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = "UPDATE audit_entries SET Hash=$hash WHERE Sequence=$sequence;";
                update.Parameters.AddWithValue("$hash", new string('0', 64));
                update.Parameters.AddWithValue("$sequence", sequence);
                Assert.Equal(1, await update.ExecuteNonQueryAsync());
            }
            await ExecuteSqlAsync(connection, trigger!);
            await ExecuteSqlAsync(connection, "COMMIT;");
        }
        catch
        {
            try { await ExecuteSqlAsync(connection, "ROLLBACK;"); }
            catch (SqliteException) { }
            throw;
        }
    }

    private sealed record PartIdentityAuditCounts(long PartIdentityRows, long AuditRows,
        long AuditTailSequence, long PartIdentityAuditSequence);

    private static async Task BackupPartIdentityDatabaseAsync(string databasePath, string backupPath)
    {
        // SQLite's snapshot includes committed WAL pages even when the main file
        // has not yet been checkpointed by the last reader.
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        await source.OpenAsync();
        await destination.OpenAsync();
        source.BackupDatabase(destination);
    }

    private static async Task ExecuteSqlAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void DeleteSqliteSidecars(string databasePath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
