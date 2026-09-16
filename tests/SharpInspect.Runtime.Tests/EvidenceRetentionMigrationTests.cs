using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ProductionOutboxMigrationTests
{
    [Theory, Trait("VerificationId", "V155_M01")]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public async Task V155_M01_Schema38To39PreservesEveryDeclaredOptionalSource(bool images, bool outbox, bool recovery)
    {
        var directory = ImageEvidenceFixture.NewDirectory("V155-M01");
        try
        {
            var trace = TracePolicies("V155.M01");
            var original = SourceProfile(directory, "V155M01", images ? 35 : 32, trace);
            if (outbox)
            {
                var delivery = OutboxFor(original);
                if (recovery) delivery = new(delivery.Routes, delivery.RecipeLifecycle, delivery.ImageEvidence,
                    delivery.ImageFinalization) { ManualRecovery = new() };
                original = Copy(original, trace, delivery);
            }
            var source = WithReconciliation(original, directory);
            await using (var writer = new SqliteCommandStore(source))
                Assert.True((await writer.Initialization).Committed);
            var before = Fingerprint(source);
            Assert.Equal(38, before.SchemaVersion);
            var target = WithRetention(source);
            var opened = await OpenAsync(target);
            Assert.True(opened.Available, opened.Status.ReasonCode);
            await using (var session = opened.Session!)
            {
                var completed = await StoreStartupMaintenanceTests.Finish(session);
                Assert.True(completed.Completed, completed.ReasonCode);
                Assert.Equal(38, completed.SourceSchemaVersion);
                Assert.Equal(39, completed.TargetSchemaVersion);
                Assert.False(completed.Ready);
            }
            using (var connection = SqliteNative.Open(source.DatabasePath, true))
                Assert.True(StoreMigrationFingerprint.ExistingRowsUnchanged(connection.Handle!, before.Tables, Budget, Deadline()));
            var last = StoreMigrationJournal.ReadChain(StoreMigrationJournalGuard.JournalPath(source.DatabasePath),
                4L * 1024 * 1024)[^1].Data;
            Assert.Equal(StoreMigrationJournal.StorageRetentionPlanId, last.PlanId);
            Assert.Equal(SqliteCommandStore.RetentionConfigurationFor(target).BindingHash, last.StorageRetentionConfigurationHash);
            await using (var reopened = new SqliteCommandStore(target))
            {
                var result = await reopened.Initialization;
                Assert.True(result.Committed, result.ReasonCode);
                var retained = await new SqliteEvidenceRetentionQuery(target).ReadAsync(new());
                Assert.True(retained.Available, retained.ReasonCode);
                Assert.Empty(retained.Records);
            }
            await using var old = new SqliteCommandStore(source);
            Assert.False((await old.Initialization).Committed);
        }
        finally { Remove(directory); }
    }

    [Fact, Trait("VerificationId", "V155_M02")]
    public async Task V155_M02_CompletedReconciliationMigrationLinksToRetentionWithoutDroppingJournal()
    {
        var directory = ImageEvidenceFixture.NewDirectory("V155-M02");
        try
        {
            var source = SourceProfile(directory, "V155M02", 35, TracePolicies("V155.M02"));
            await using (var writer = new SqliteCommandStore(source))
                Assert.True((await writer.Initialization).Committed);
            var reconciled = WithReconciliation(source, directory);
            var first = await OpenAsync(reconciled);
            Assert.True(first.Available, first.Status.ReasonCode);
            await using (var session = first.Session!) Assert.True((await StoreStartupMaintenanceTests.Finish(session)).Completed);
            var journal = StoreMigrationJournalGuard.JournalPath(source.DatabasePath);
            var prefix = File.ReadAllBytes(journal);
            var predecessor = StoreMigrationJournal.ReadChain(journal, 4L * 1024 * 1024)[^1];
            var second = await OpenAsync(WithRetention(reconciled));
            Assert.True(second.Available, second.Status.ReasonCode);
            await using (var session = second.Session!) Assert.True((await StoreStartupMaintenanceTests.Finish(session)).Completed);
            Assert.True(File.ReadAllBytes(journal).AsSpan(0, prefix.Length).SequenceEqual(prefix));
            var last = StoreMigrationJournal.ReadChain(journal, 4L * 1024 * 1024)[^1];
            Assert.Equal(predecessor.Data.OperationId, last.Data.PreviousOperationId);
            Assert.Equal(predecessor.ContentHash, last.Data.PreviousOperationJournalHash);
            Assert.Equal(39, last.Data.TargetSchemaVersion);
        }
        finally { Remove(directory); }
    }

    private static ProductionStoreOptions WithRetention(ProductionStoreOptions source)
    {
        var execution = new TraceRetentionExecutionPolicy("V155.Migration", "1", "controlled fixture", "Retain original evidence",
            Array.Empty<TraceRetentionClass>(), new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), 1024 * 1024, 4), 3);
        var target = new ProductionStoreOptions
            { StorageRetention = new(execution, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), new(128L << 20, 10000, 1L << 20)) };
        foreach (var property in typeof(ProductionStoreOptions).GetProperties())
            if (property.Name != nameof(ProductionStoreOptions.StorageRetention)) property.SetValue(target, property.GetValue(source));
        return target;
    }

    public static IEnumerable<object[]> RetentionMigrationPhases => Enumerable.Range(1, 14)
        .Select(value => new object[] { (StoreMigrationPhase)value });

    [Theory, MemberData(nameof(RetentionMigrationPhases)), Trait("VerificationId", "V155_M03")]
    public async Task V155_M03_EveryMigrationPhaseCanCloseAndResumeWithoutChangingSourceFacts(StoreMigrationPhase phase)
    {
        var directory = ImageEvidenceFixture.NewDirectory("V155-M03-" + phase);
        try
        {
            var source = WithReconciliation(SourceProfile(directory, "V155M03", 35, TracePolicies("V155.M03")), directory);
            await using (var writer = new SqliteCommandStore(source)) Assert.True((await writer.Initialization).Committed);
            var original = Fingerprint(source);
            var target = WithRetention(source);
            var opened = await OpenAsync(target);
            Assert.True(opened.Available, opened.Status.ReasonCode);
            await using (var session = opened.Session!)
            {
                for (var step = 0; session.Status.Phase != phase && step < 20; step++)
                {
                    var advanced = await session.AdvanceAsync();
                    Assert.NotEqual(StoreMigrationPhase.MaintenanceRequired, advanced.Phase);
                }
                Assert.Equal(phase, session.Status.Phase);
            }
            var resumed = await OpenAsync(target);
            Assert.True(resumed.Available, resumed.Status.ReasonCode);
            await using (var session = resumed.Session!) Assert.True((await StoreStartupMaintenanceTests.Finish(session)).Completed);
            using var connection = SqliteNative.Open(source.DatabasePath, true);
            Assert.True(StoreMigrationFingerprint.ExistingRowsUnchanged(connection.Handle!, original.Tables, Budget, Deadline()));
            Assert.Equal(39, Scalar(source.DatabasePath, "PRAGMA user_version;"));
        }
        finally { Remove(directory); }
    }
}
