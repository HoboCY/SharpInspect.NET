using SharpInspect.Abstractions;
using SharpInspect.Runtime.Evidence;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ProductionOutboxMigrationTests
{
    [Fact, Trait("VerificationId", "V154_R07")]
    public async Task V154_R07_ZeroCommitImageTimeoutCannotStarveAnExistingOutboxScrubRun()
    {
        var directory = ImageEvidenceFixture.NewDirectory("V154-R07");
        try
        {
            var trace = TracePolicies("V154.R07");
            var images = SourceProfile(directory, "V154R07", 35, trace);
            var target = WithReconciliation(Copy(images, trace, OutboxFor(images)), directory);
            await using var store = new SqliteCommandStore(target);
            var initialized = await store.Initialization;
            Assert.True(initialized.Committed, initialized.ReasonCode);
            var faults = new List<string>();
            var worker = new EvidenceReconciliationWorker(store, target, Guid.NewGuid(), store.Initialization,
                Task.CompletedTask, reason => { faults.Add(reason); return Task.CompletedTask; }, startScrubber: false);
            try
            {
                await worker.Startup.WaitAsync(TimeSpan.FromSeconds(15));
                EvidenceReconciliationPayload Start(EvidenceReconciliationStream stream) =>
                    new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EvidenceReconciliationPhase.HistoricalScrub,
                        EvidenceReconciliationEventKind.RunStarted, stream, DateTimeOffset.UtcNow, "FairnessRunStarted",
                        null, null, 0, 0, 0, 0, 0, 0);
                var imageRun = Start(EvidenceReconciliationStream.Images);
                var outboxRun = Start(EvidenceReconciliationStream.Outbox);
                var begun = await store.AppendEvidenceReconciliationAsync(new[] { imageRun, outboxRun }, null, Deadline());
                Assert.True(begun.Committed, begun.ReasonCode);
                var query = new SqliteEvidenceReconciliationQuery(target);
                var before = await query.ReadAsync(new());
                store.EvidenceReconciliationAfterReadProof = () => throw new TimeoutException("Controlled zero-commit image timeout");
                await worker.RunScrubTurnAsync();
                store.EvidenceReconciliationAfterReadProof = null;
                var yielded = await query.ReadAsync(new());
                Assert.Equal(before.Records.Select(x => x.ContentHash), yielded.Records.Select(x => x.ContentHash));
                await worker.RunScrubTurnAsync();
                var outboxDone = await query.ReadAsync(new());
                Assert.Contains(outboxDone.Records, x => x.RunId == outboxRun.RunId &&
                    x.Kind == EvidenceReconciliationEventKind.RunCompleted);
                Assert.DoesNotContain(outboxDone.Records, x => x.RunId == imageRun.RunId &&
                    x.Kind == EvidenceReconciliationEventKind.RunCompleted);
                await worker.RunScrubTurnAsync();
                var imagesDone = await query.ReadAsync(new());
                Assert.Contains(imagesDone.Records, x => x.RunId == imageRun.RunId &&
                    x.Kind == EvidenceReconciliationEventKind.RunCompleted);
                Assert.Empty(faults);
            }
            finally
            {
                store.EvidenceReconciliationAfterReadProof = null;
                Assert.True(await worker.StopAsync());
            }
        }
        finally { Remove(directory); }
    }
}
