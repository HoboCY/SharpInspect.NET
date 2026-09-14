using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ProductionOutboxMigrationTests
{
    [Theory]
    [InlineData(35, StoreMigrationJournal.EvidenceReconciliation35PlanId)]
    [InlineData(36, StoreMigrationJournal.EvidenceReconciliation36PlanId)]
    [InlineData(37, StoreMigrationJournal.EvidenceReconciliation37PlanId)]
    [Trait("VerificationId", "V154_M01")]
    public async Task V154_M01_EachSourceProfileMigratesTo38WithoutChangingOldRows(int generation, string plan)
    {
        var directory = ImageEvidenceFixture.NewDirectory("V154-M01-" + generation);
        try
        {
            var trace = TracePolicies("V154.M01");
            var source = SourceProfile(directory, "V154M01" + generation, 35, trace);
            if (generation >= 36)
            {
                var outbox = OutboxFor(source);
                if (generation == 37)
                    outbox = new(outbox.Routes, outbox.RecipeLifecycle, outbox.ImageEvidence, outbox.ImageFinalization)
                        { ManualRecovery = new() };
                source = Copy(source, trace, outbox);
            }
            await using (var writer = new SqliteCommandStore(source))
            {
                var initialized = await writer.Initialization;
                Assert.True(initialized.Committed, initialized.ReasonCode);
            }
            var original = Fingerprint(source);
            Assert.Equal(generation, original.SchemaVersion);
            var target = WithReconciliation(source, directory);
            var opened = await OpenAsync(target);
            Assert.True(opened.Available, opened.Status.ReasonCode);
            await using (var session = opened.Session!)
            {
                var completed = await StoreStartupMaintenanceTests.Finish(session);
                Assert.True(completed.Completed, completed.ReasonCode);
                Assert.Equal(generation, completed.SourceSchemaVersion);
                Assert.Equal(38, completed.TargetSchemaVersion);
                Assert.False(completed.Ready);
            }
            using (var connection = SqliteNative.Open(source.DatabasePath, readOnly: true))
                Assert.True(StoreMigrationFingerprint.ExistingRowsUnchanged(connection.Handle!, original.Tables,
                    Budget, Deadline()));
            var last = StoreMigrationJournal.ReadChain(StoreMigrationJournalGuard.JournalPath(source.DatabasePath),
                4L * 1024 * 1024)[^1].Data;
            Assert.Equal(plan, last.PlanId);
            Assert.Equal(SqliteCommandStore.ReconciliationConfiguration(target).BindingHash,
                last.EvidenceReconciliationConfigurationHash);
            Assert.Equal(source.Outbox?.ManualRecovery?.BindingHash, last.ProductionOutboxRecoveryConfigurationHash);
            await using var reopened = new SqliteCommandStore(target);
            var initializedAgain = await reopened.Initialization;
            Assert.True(initializedAgain.Committed, initializedAgain.ReasonCode);
            var snapshot = await new SqliteEvidenceReconciliationQuery(target).ReadAsync(new());
            Assert.True(snapshot.Available, snapshot.ReasonCode);
            Assert.Empty(snapshot.Records);
            Assert.Null(snapshot.LatestStartup); // Migration itself never certifies runtime startup.
            Assert.Equal(generation >= 36 ? 1L : 0L, Scalar(target.DatabasePath,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='production_outbox_store_config';"));
            Assert.Equal(generation == 37 ? 1L : 0L, Scalar(target.DatabasePath,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='production_outbox_recovery_config';"));
        }
        finally { Remove(directory); }
    }

    internal static ProductionStoreOptions WithReconciliation(ProductionStoreOptions source, string directory)
    {
        EvidenceQuarantineRootOptions? stage = null;
        EvidenceQuarantineRootOptions? final = null;
        long bytes = 1024 * 1024;
        if (source.ImageFinalization is { } images)
        {
            var stagePath = Path.Combine(directory, "stage-quarantine");
            var finalPath = Path.Combine(directory, "final-quarantine");
            Directory.CreateDirectory(stagePath); Directory.CreateDirectory(finalPath);
            stage = new(stagePath, images.ImageEvidence.Stage.MaximumStageBytes,
                Math.Max(64L * 1024 * 1024, images.ImageEvidence.Stage.MaximumStageBytes), 256);
            final = new(finalPath, images.FinalRoot.MaximumFinalFileBytes,
                Math.Max(64L * 1024 * 1024, images.FinalRoot.MaximumFinalFileBytes), 256);
            bytes = checked(images.FinalRoot.MaximumFinalFileBytes + images.ImageEvidence.Stage.MaximumStageBytes);
        }
        var target = new ProductionStoreOptions
        {
            EvidenceReconciliation = new(new TraceStorageMaintenanceBudget(TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5), bytes, 16)) { StageQuarantine = stage, FinalQuarantine = final }
        };
        foreach (var property in typeof(ProductionStoreOptions).GetProperties())
            if (property.Name != nameof(ProductionStoreOptions.EvidenceReconciliation))
                property.SetValue(target, property.GetValue(source));
        return target;
    }
}
