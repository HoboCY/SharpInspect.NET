using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Bounded storage checks for the schema-27 PLC communication ledger. These tests
/// use the real signed store and read-only history query; they do not exercise the
/// Modbus owner, which has its own transport and watchdog coverage.
/// </summary>
public sealed class PlcCommunicationStorageTests
{
    [Fact]
    public async Task V141_S01_InitializesAppendsAndColdReopensSignedHistory()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var start = DateTimeOffset.Parse("2026-09-11T00:00:00Z");

        Assert.True((await fixture.AppendAsync(PlcCommunicationEventKind.ConnectionEstablished,
            1, start, attempt: 0)).Committed);
        Assert.True((await fixture.AppendAsync(PlcCommunicationEventKind.SynchronizationWindowObserved,
            2, start.AddSeconds(1), attempt: 1)).Committed);
        Assert.True((await fixture.AppendAsync(PlcCommunicationEventKind.RecoveryCompleted,
            3, start.AddSeconds(2), attempt: 2)).Committed);

        var query = new SqlitePlcCommunicationHistoryQuery(fixture.Options);
        var current = await query.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Equal(3, current.Latest!.Position);
        Assert.Equal(PlcCommunicationEventKind.RecoveryCompleted, current.Latest.Kind);

        var before = await query.QueryAsync(new(PageSize: 20));
        Assert.True(before.Available, before.ReasonCode);
        Assert.Equal(3, before.Events.Count);
        Assert.All(before.Events, value => Assert.Equal(SystemPrincipalId.PlcAdapter, value.SystemPrincipalId));
        var hashes = before.Events.Select(value => value.ContentHash).ToArray();

        await fixture.ReopenAsync();
        var after = await new SqlitePlcCommunicationHistoryQuery(fixture.Options)
            .QueryAsync(new(PageSize: 20));
        Assert.True(after.Available, after.ReasonCode);
        Assert.Equal(hashes, after.Events.Select(value => value.ContentHash).ToArray());
        Assert.Equal(before.ThroughPosition, after.ThroughPosition);
    }

    [Fact]
    public async Task V141_S02_TamperingMakesColdHistoryUnavailable()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        Assert.True((await fixture.AppendAsync(PlcCommunicationEventKind.ConnectionEstablished,
            1, DateTimeOffset.Parse("2026-09-11T00:00:00Z"))).Committed);

        await fixture.CloseStoreAsync();
        await fixture.TamperFirstReasonAsync();

        var result = await new SqlitePlcCommunicationHistoryQuery(fixture.Options)
            .ReadCurrentAsync();
        Assert.False(result.Available);
        Assert.NotEqual("PlcCommunicationHistoryAvailable", result.ReasonCode);
    }

    [Fact]
    public async Task V141_S03_EntryCapacityRejectsAtomically()
    {
        await using var fixture = await StoreFixture.CreateAsync(new PlcCommunicationStoreOptions
        {
            MaximumEntries = 1,
            MaximumPayloadBytes = 64 * 1024,
            MaximumTotalBytes = 256 * 1024
        });

        var first = await fixture.AppendAsync(PlcCommunicationEventKind.ConnectionEstablished,
            1, DateTimeOffset.Parse("2026-09-11T00:00:00Z"));
        Assert.True(first.Committed, first.ReasonCode);
        var auditCount = await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;");

        var rejected = await fixture.AppendAsync(PlcCommunicationEventKind.HeartbeatStale,
            2, DateTimeOffset.Parse("2026-09-11T00:00:01Z"), attempt: 1);
        Assert.False(rejected.Committed);
        Assert.Equal("PlcCommunicationEntryCapacityExceeded", rejected.ReasonCode);
        Assert.Equal(1, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM plc_communication_events;"));
        Assert.Equal(auditCount, await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;"));

        var page = await new SqlitePlcCommunicationHistoryQuery(fixture.Options)
            .QueryAsync(new(PageSize: 20));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Single(page.Events);
        Assert.Equal(1, page.Events[0].Position);
    }

    [Fact]
    public async Task V141_S04_ThroughPositionAndEndpointRecoveryFiltersAreStable()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var start = DateTimeOffset.Parse("2026-09-11T00:00:00Z");
        Assert.True((await fixture.AppendAsync(PlcCommunicationEventKind.HeartbeatStale,
            1, start, endpoint: fixture.EndpointA, recovery: fixture.RecoveryA)).Committed);
        Assert.True((await fixture.AppendAsync(PlcCommunicationEventKind.ControllerEpochObserved,
            2, start.AddSeconds(1), endpoint: fixture.EndpointA, recovery: fixture.RecoveryA,
            attempt: 1)).Committed);
        Assert.True((await fixture.AppendAsync(PlcCommunicationEventKind.RecoveryCompleted,
            3, start.AddSeconds(2), endpoint: fixture.EndpointB, recovery: fixture.RecoveryB,
            attempt: 2)).Committed);

        var query = new SqlitePlcCommunicationHistoryQuery(fixture.Options);
        var first = await query.QueryAsync(new(
            EndpointBindingHash: fixture.EndpointA,
            RecoveryCycleId: fixture.RecoveryA,
            ThroughPosition: 2,
            PageSize: 1));
        Assert.True(first.Available, first.ReasonCode);
        Assert.Single(first.Events);
        Assert.Equal(1, first.Events[0].Position);
        Assert.Equal(2, first.ThroughPosition);
        Assert.Equal(1, first.NextAfterPosition);
        Assert.True(first.RecoveryRequired);
        Assert.True((await query.ReadCurrentAsync()).RecoveryRequired);

        var second = await query.QueryAsync(new(
            EndpointBindingHash: fixture.EndpointA,
            RecoveryCycleId: fixture.RecoveryA,
            AfterPosition: first.NextAfterPosition!.Value,
            ThroughPosition: first.ThroughPosition,
            PageSize: 1));
        Assert.True(second.Available, second.ReasonCode);
        Assert.Single(second.Events);
        Assert.Equal(2, second.Events[0].Position);
        Assert.Null(second.NextAfterPosition);

        var other = await query.QueryAsync(new(
            EndpointBindingHash: fixture.EndpointB,
            RecoveryCycleId: fixture.RecoveryB,
            PageSize: 20));
        Assert.True(other.Available, other.ReasonCode);
        Assert.Single(other.Events);
        Assert.Equal(3, other.Events[0].Position);
        Assert.False(other.RecoveryRequired);
        Assert.True((await fixture.AppendAsync(PlcCommunicationEventKind.RecoveryCompleted,
            4, start.AddSeconds(3), endpoint: fixture.EndpointA, recovery: fixture.RecoveryA,
            attempt: 3)).Committed);
        var frozen = await query.QueryAsync(new(EndpointBindingHash: fixture.EndpointA,
            RecoveryCycleId: fixture.RecoveryA, ThroughPosition: 2, PageSize: 20));
        Assert.True(frozen.Available, frozen.ReasonCode);
        Assert.True(frozen.RecoveryRequired);
        Assert.False((await query.ReadCurrentAsync()).RecoveryRequired);
    }

    [Fact]
    public async Task V141_S05_ObservedWindowAndInitialEpochDoNotClearOrCreatePendingRecovery()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var staleAt = DateTimeOffset.Parse("2026-09-11T00:00:10Z");
        Assert.True((await fixture.AppendAsync(PlcCommunicationEventKind.HeartbeatStale,
            10, staleAt, recovery: fixture.RecoveryA)).Committed);
        // The wall clock has moved backwards, but the monotonic source remains
        // ordered. A valid audit history must retain this observation unchanged.
        Assert.True((await fixture.AppendAsync(PlcCommunicationEventKind.SynchronizationWindowObserved,
            11, staleAt.AddSeconds(-1), recovery: fixture.RecoveryA, attempt: 1)).Committed);

        var query = new SqlitePlcCommunicationHistoryQuery(fixture.Options);
        var pending = await query.ReadCurrentAsync();
        Assert.True(pending.Available, pending.ReasonCode);
        Assert.True(pending.RecoveryRequired);
        Assert.Equal(PlcCommunicationEventKind.SynchronizationWindowObserved,
            pending.Latest!.Kind);
        Assert.Equal(staleAt.AddSeconds(-1), pending.Latest.ObservedAtUtc);

        Assert.True((await fixture.AppendAsync(PlcCommunicationEventKind.ControllerEpochObserved,
            12, staleAt.AddSeconds(-2), recovery: fixture.RecoveryB, attempt: 2)).Committed);
        var initialJitter = await query.QueryAsync(new(
            RecoveryCycleId: fixture.RecoveryB, PageSize: 20));
        Assert.True(initialJitter.Available, initialJitter.ReasonCode);
        Assert.False(initialJitter.RecoveryRequired);
        Assert.Single(initialJitter.Events);
        Assert.Equal(PlcCommunicationEventKind.ControllerEpochObserved,
            initialJitter.Events[0].Kind);
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly AuditIntegrityPolicy _audit;
        private bool _disposed;

        private StoreFixture(string directory, ProductionStoreOptions options,
            AuditIntegrityPolicy audit, SqliteCommandStore store)
        {
            _directory = directory;
            Options = options;
            _audit = audit;
            Store = store;
        }

        internal ProductionStoreOptions Options { get; }
        internal SqliteCommandStore? Store { get; private set; }
        internal string EndpointA { get; } = Hash("plc-endpoint-a");
        internal string EndpointB { get; } = Hash("plc-endpoint-b");
        internal Guid RecoveryA { get; } = Guid.Parse("14100000-0000-0000-0000-000000000001");
        internal Guid RecoveryB { get; } = Guid.Parse("14100000-0000-0000-0000-000000000002");
        internal Guid SessionId { get; } = Guid.Parse("14100000-0000-0000-0000-000000000003");
        internal Guid RunId { get; } = Guid.Parse("14100000-0000-0000-0000-000000000004");
        internal Guid RuntimeEpoch { get; } = Guid.Parse("14100000-0000-0000-0000-000000000005");
        internal string ProfileHash { get; } = Hash("plc-profile");
        internal string PolicyHash { get; } = Hash("plc-policy");

        internal static async Task<StoreFixture> CreateAsync(
            PlcCommunicationStoreOptions? plcOptions = null)
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("PLC storage requires Windows machine-key protection.");

            var directory = Path.Combine(Path.GetTempPath(),
                "SharpInspect.NET-validation-artifacts", "ticket41-plc",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var audit = new AuditIntegrityPolicy("V141PlcStorage", "1",
                "SharpInspect.Test.V141.Plc." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "keys"),
                CheckpointEveryEntries = 2,
                MaximumVerificationEntries = 10_000,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            var identity = new LocalIdentityOptions("V141PlcStorage",
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("V141-plc", "1",
                        new[] { "known-compromised-value" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                AuthorizationPolicy.Development);
            var options = new ProductionStoreOptions(Path.Combine(directory, "plc.sqlite"))
            {
                AuditIntegrityPolicy = audit,
                LocalIdentity = identity,
                PlcCommunication = plcOptions ?? new PlcCommunicationStoreOptions(),
                CommitTimeout = TimeSpan.FromSeconds(5),
                QueryTimeout = TimeSpan.FromSeconds(5),
                QueueCapacity = 8
            };
            SqliteCommandStore? store = null;
            try
            {
                store = new SqliteCommandStore(options);
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);
                return new StoreFixture(directory, options, audit, store);
            }
            catch
            {
                if (store is not null) await store.DisposeAsync();
                Cleanup(directory, audit);
                throw;
            }
        }

        internal ValueTask<StoreWriteResult> AppendAsync(PlcCommunicationEventKind kind,
            long monotonicTimestamp, DateTimeOffset observedAtUtc, string? endpoint = null,
            Guid? recovery = null, int attempt = 0, long generation = 0,
            uint? controllerEpoch = 1, string? reason = null,
            Guid? session = null, Guid? run = null) =>
            Store!.AppendPlcCommunicationEventAsync(new PlcCommunicationWriteRequest(
                RuntimeEpoch, endpoint ?? EndpointA, ProfileHash, PolicyHash, generation, attempt,
                kind, reason ?? Reason(kind), controllerEpoch, observedAtUtc, monotonicTimestamp,
                recovery ?? RecoveryA, session ?? SessionId, run ?? RunId, 1),
                new StoreDeadline(TimeSpan.FromSeconds(5)));

        internal async Task ReopenAsync()
        {
            await CloseStoreAsync();
            Store = new SqliteCommandStore(Options);
            var initialized = await Store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitForVerifiedAsync(Store);
        }

        internal async Task CloseStoreAsync()
        {
            if (Store is null) return;
            await Store.DisposeAsync();
            Store = null;
        }

        internal async Task TamperFirstReasonAsync()
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Options.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "DROP TRIGGER plc_communication_event_immutable_update; " +
                "UPDATE plc_communication_events SET ReasonCode='PlcTampered' WHERE Position=1;";
            await command.ExecuteNonQueryAsync();
        }

        internal async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Options.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await CloseStoreAsync();
            Cleanup(_directory, _audit);
        }

        private static async Task WaitForVerifiedAsync(SqliteCommandStore store)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (store.Integrity is { State: AuditIntegrityState.Verified }) return;
                if (store.Integrity is { State: AuditIntegrityState.Faulted } fault)
                    throw new XunitException(fault.ReasonCode);
                await Task.Delay(25);
            }
            throw new XunitException("Audit integrity did not become Verified: " +
                store.Integrity?.ReasonCode);
        }

        private static string Reason(PlcCommunicationEventKind kind) => kind switch
        {
            PlcCommunicationEventKind.ConnectionEstablished => "PlcTransportConnected",
            PlcCommunicationEventKind.HeartbeatStale => "PlcControllerHeartbeatStale",
            PlcCommunicationEventKind.ControllerEpochObserved => "PlcControllerEpochObserved",
            PlcCommunicationEventKind.SynchronizationWindowObserved =>
                "PlcSynchronizationWindowObserved",
            PlcCommunicationEventKind.RecoveryCompleted => "PlcCommunicationRecoveredArmRequired",
            _ => "PlcCommunicationObserved"
        };

        private static string Hash(string value) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        private static void Cleanup(string directory, AuditIntegrityPolicy policy)
        {
            try
            {
                var keyPath = WindowsMachineAuditKey.GetKeyPath(policy);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
