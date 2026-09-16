using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class EvidenceRetentionStorageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("VerificationId", "V155_S01")]
    public async Task V155_S01_ColdSchema39ReopensWithSignedConfigurationAndRejectsOldWriter(bool recovery)
    {
        using var fixture = new ProductionOutboxStorageTests.OutboxStoreFixture();
        var options = Options(fixture, recovery);
        await using (var store = new SqliteCommandStore(options))
        {
            var initialized = await store.Initialization;
            Assert.True(initialized.Committed, initialized.ReasonCode);
        }
        await using (var store = new SqliteCommandStore(options))
        {
            var initialized = await store.Initialization;
            Assert.True(initialized.Committed, initialized.ReasonCode);
            var snapshot = await new SqliteEvidenceRetentionQuery(options).ReadAsync(new());
            Assert.True(snapshot.Available, snapshot.ReasonCode);
            Assert.Empty(snapshot.Records);
            Assert.Equal(0, snapshot.DeletedFiles);
            using var connection = SqliteNative.Open(fixture.DatabasePath, readOnly: true);
            Assert.Equal(39, AuditChainDatabase.Scalar(connection.Handle!, "PRAGMA user_version;", Deadline()));
        }
        await using var old = new SqliteCommandStore(EvidenceReconciliationStorageTests.Options(fixture, recovery));
        Assert.False((await old.Initialization).Committed);
    }

    [Fact]
    [Trait("VerificationId", "V155_S02")]
    public async Task V155_S02_ChangedExecutionPolicyCannotOpenOrQueryExistingStore()
    {
        using var fixture = new ProductionOutboxStorageTests.OutboxStoreFixture();
        await using (var store = new SqliteCommandStore(Options(fixture)))
        {
            var initialized = await store.Initialization;
            Assert.True(initialized.Committed, initialized.ReasonCode);
        }
        var changed = Options(fixture, version: "2");
        var result = await new SqliteEvidenceRetentionQuery(changed).ReadAsync(new());
        Assert.False(result.Available);
        Assert.Equal("RetentionConfigurationMismatch", result.ReasonCode);
        await using var refused = new SqliteCommandStore(changed);
        Assert.False((await refused.Initialization).Committed);
    }

    internal static ProductionStoreOptions Options(ProductionOutboxStorageTests.OutboxStoreFixture fixture,
        bool recovery = false, string version = "1") => new(fixture.DatabasePath)
    {
        AuditIntegrityPolicy = fixture.Policy, LocalIdentity = fixture.Identity,
        ProductionInspections = fixture.Inspections, TraceStoragePolicies = fixture.TracePolicies,
        Outbox = new ProductionOutboxStoreOptions(fixture.Outbox().Routes)
            { ManualRecovery = recovery ? new ProductionOutboxRecoveryOptions() : null },
        EvidenceReconciliation = new(new TraceStorageMaintenanceBudget(TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5), 1024 * 1024, 16)) { MaximumEvents = 100 },
        StorageRetention = new(new TraceRetentionExecutionPolicy("retention-test", version,
            "fixture-approval", "Controlled test; no owned image deletion enabled.",
            Array.Empty<TraceRetentionClass>(), new(TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(2), 1024 * 1024, 4), 3),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), new(128L << 20, 10000, 1L << 20)),
        CommitTimeout = TimeSpan.FromSeconds(10), QueryTimeout = TimeSpan.FromSeconds(10)
    };
    private static StoreDeadline Deadline() => new(TimeSpan.FromSeconds(10));
}
