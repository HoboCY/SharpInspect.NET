using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ProductionOutboxMigrationTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(35)]
    [Trait("VerificationId", "V153_M04")]
    public async Task V153_M04_EveryCompletedOutboxMigrationCanContinueIntoGovernedRecovery(int generation)
    {
        var directory = ImageEvidenceFixture.NewDirectory("V153-M04-" + generation);
        try
        {
            var policies = TracePolicies("V153.M04");
            var source = SourceProfile(directory, "V153M04" + generation, generation, policies);
            await using (var writer = new SqliteCommandStore(source))
                Assert.True((await writer.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
            var outbox = OutboxFor(source);
            var target36 = Copy(source, policies, outbox);
            var first = await OpenAsync(target36);
            Assert.True(first.Available, first.Status.ReasonCode);
            Guid predecessor;
            await using (var session = first.Session!)
            {
                var completed = await StoreStartupMaintenanceTests.Finish(session);
                Assert.True(completed.Completed, completed.ReasonCode);
                predecessor = completed.OperationId;
            }
            var journalPath = StoreMigrationJournalGuard.JournalPath(target36.DatabasePath);
            var oldJournal = File.ReadAllBytes(journalPath);
            var fingerprint = Fingerprint(target36);
            var target37 = Copy(target36, policies, new ProductionOutboxStoreOptions(outbox.Routes,
                outbox.RecipeLifecycle, outbox.ImageEvidence, outbox.ImageFinalization)
            {
                MaximumAttempts = outbox.MaximumAttempts, MaximumRetryDelay = outbox.MaximumRetryDelay,
                AttemptTimeout = outbox.AttemptTimeout, MaximumEvents = outbox.MaximumEvents,
                MaximumPayloadBytes = outbox.MaximumPayloadBytes, MaximumTotalBytes = outbox.MaximumTotalBytes,
                MaximumPageSize = outbox.MaximumPageSize, ManualRecovery = new()
            });
            var second = await OpenAsync(target37);
            Assert.True(second.Available, second.Status.ReasonCode);
            var operation = second.Status.OperationId;
            Assert.NotEqual(predecessor, operation);
            // Resume a linked operation after a durable public boundary, preserving its ID.
            await StoreStartupMaintenanceTests.AdvanceTo(second.Session!, StoreMigrationPhase.BackupVerified);
            await second.Session!.DisposeAsync();
            var resumed = await OpenAsync(target37);
            Assert.True(resumed.Available, resumed.Status.ReasonCode);
            Assert.Equal(operation, resumed.Status.OperationId);
            await using (var session = resumed.Session!)
                Assert.True((await StoreStartupMaintenanceTests.Finish(session)).Completed);
            Assert.True(File.ReadAllBytes(journalPath).AsSpan(0, oldJournal.Length).SequenceEqual(oldJournal));
            var chain = StoreMigrationJournal.ReadChain(journalPath, 4L * 1024 * 1024);
            Assert.Equal(StoreMigrationJournal.ProductionOutboxRecoveryPlanId, chain[^1].Data.PlanId);
            Assert.Equal(predecessor, chain[^1].Data.PreviousOperationId);
            Assert.Equal(37, Scalar(target37.DatabasePath, "PRAGMA user_version;"));
            using (var connection = SqliteNative.Open(target37.DatabasePath, readOnly: true))
                Assert.True(StoreMigrationFingerprint.ExistingRowsUnchanged(connection.Handle!,
                    fingerprint.Tables, Budget, Deadline()));
            await using var current = new SqliteCommandStore(target37);
            var initialized = await current.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(initialized.Committed, initialized.ReasonCode);
        }
        finally { Remove(directory); }
    }
}
