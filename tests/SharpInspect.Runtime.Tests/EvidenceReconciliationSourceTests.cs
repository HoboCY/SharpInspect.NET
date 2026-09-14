using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Evidence;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact, Trait("VerificationId", "V154_S04")]
    public async Task V154_S04_NonemptySourceCannotBeSkippedOrCertifiedWithAnOldStartupRevision()
    {
        await using var fixture = await OutboxCoreFixture.CreateAsync();
        await fixture.CommitAsync();
        await CloseReconciliationSourceCycleAsync(fixture);
        var delivery = fixture.Batch.Deliveries[0];
        var originalBytes = delivery.Payload!.CopyBytes();
        await fixture.Store.DisposeAsync();
        var target = ProductionOutboxMigrationTests.WithReconciliation(fixture.Options,
            Path.GetDirectoryName(fixture.Options.DatabasePath)!);
        await MigrateReconciliationAsync(target);
        var store = new SqliteCommandStore(target);
        try
        {
            var initialized = await store.Initialization;
            Assert.True(initialized.Committed, initialized.ReasonCode);
            var query = new SqliteEvidenceReconciliationQuery(target);
            var source = Assert.Single(await query.ReadSourcesAsync(EvidenceReconciliationSubjectKind.Outbox, 0, long.MaxValue, 16, default));
            var historical = ReconciliationStart(EvidenceReconciliationStream.Outbox, source.SourcePosition!.Value);
            var begun = await AppendReconciliationFactsAsync(store, new[] { historical });
            Assert.True(begun.Committed, begun.ReasonCode);
            var skipped = historical with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.RunCompleted,
                PreviousCheckpointHash = begun.Records![0].ContentHash, AfterSourcePosition = historical.ThroughSourcePosition };
            var refused = await AppendReconciliationFactsAsync(store, new[] { skipped });
            Assert.False(refused.Committed);
            Assert.Equal("EvidenceReconciliationSourceCoverageIncomplete", refused.ReasonCode);
            var startup = ReconciliationStart(null, 0);
            var started = await AppendReconciliationFactsAsync(store, new[] { startup });
            Assert.True(started.Committed, started.ReasonCode);
            var startupFinish = startup with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.RunCompleted,
                PreviousCheckpointHash = started.Records![0].ContentHash };
            var missing = await AppendReconciliationFactsAsync(store, new[] { startupFinish });
            Assert.False(missing.Committed);
            Assert.Equal("EvidenceReconciliationStartupCoverageIncomplete", missing.ReasonCode);

            var observed = historical with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.OutboxVerified,
                Subject = source, ReasonCode = "HistoricalSourceVerified" };
            var historicalComplete = skipped with { EventId = Guid.NewGuid(), ScannedItems = 1, VerifiedItems = 1 };
            var completed = await AppendReconciliationFactsAsync(store, new[] { observed, historicalComplete });
            Assert.True(completed.Committed, completed.ReasonCode);

            var observation = startup with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.OutboxVerified,
                Subject = source, ReasonCode = "StartupSourceVerified" };
            var page = startupFinish with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.PageCompleted,
                ScannedItems = 1, VerifiedItems = 1 };
            var seen = await AppendReconciliationFactsAsync(store, new[] { observation, page });
            Assert.True(seen.Committed, seen.ReasonCode);
            var blocked = await store.BlockOutboxHandlerAsync(new(delivery.DeliveryId, Assert.Single((await new SqliteProductionOutboxQuery(target).ReadPendingAsync()).Items).StateRevisionHash,
                DateTimeOffset.UtcNow, "OutboxRequiredHistoricalHandlerMissing"), ReconciliationDeadline());
            Assert.True(blocked.Committed, blocked.ReasonCode);
            startupFinish = startupFinish with { EventId = Guid.NewGuid(), PreviousCheckpointHash = seen.Records![^1].ContentHash };
            var stale = await AppendReconciliationFactsAsync(store, new[] { startupFinish });
            Assert.False(stale.Committed);
            Assert.Equal("EvidenceReconciliationStartupCoverageIncomplete", stale.ReasonCode);
            var current = Assert.Single(await query.ReadSourcesAsync(EvidenceReconciliationSubjectKind.Outbox, 0, long.MaxValue, 16, default));
            Assert.NotEqual(source.ObservedLifecycleHash, current.ObservedLifecycleHash);
            observation = observation with { EventId = Guid.NewGuid(), Subject = current };
            page = page with { EventId = Guid.NewGuid(), PreviousCheckpointHash = seen.Records[^1].ContentHash };
            seen = await AppendReconciliationFactsAsync(store, new[] { observation, page });
            Assert.True(seen.Committed, seen.ReasonCode);
            startupFinish = startupFinish with { EventId = Guid.NewGuid(), PreviousCheckpointHash = seen.Records![^1].ContentHash };
            var final = await AppendReconciliationFactsAsync(store, new[] { startupFinish });
            Assert.True(final.Committed, final.ReasonCode);
        }
        finally { await store.DisposeAsync(); }
        await using var reopened = new SqliteCommandStore(target);
        var cold = await reopened.Initialization;
        Assert.True(cold.Committed, cold.ReasonCode);
        var snapshot = await new SqliteEvidenceReconciliationQuery(target).ReadAsync(new());
        Assert.True(snapshot.Available, snapshot.ReasonCode);
        Assert.True(snapshot.LatestStartup!.Completed);
        Assert.True(snapshot.Scrubber!.Completed);
        var pending = await new SqliteProductionOutboxQuery(target).ReadPendingAsync();
        Assert.True(pending.Available, pending.ReasonCode);
        Assert.Equal(originalBytes, Assert.Single(pending.Items).Delivery.Payload!.CopyBytes());
        Assert.True(pending.Items[0].PermanentBlock);
    }

    [Fact, Trait("VerificationId", "V154_R01")]
    public async Task V154_R01_StartupWaitsForLocalOutboxAndScrubbingDoesNotSendOrRewriteHistory()
    {
        using var fixture = new ProductionOutboxStorageTests.OutboxStoreFixture();
        var options = EvidenceReconciliationStorageTests.Options(fixture);
        await using var store = new SqliteCommandStore(options);
        Assert.True((await store.Initialization).Committed);
        var local = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var faults = new List<string>();
        var worker = new EvidenceReconciliationWorker(store, options, Guid.NewGuid(), Task.CompletedTask,
            local.Task, reason => { faults.Add(reason); return Task.CompletedTask; }, startScrubber: false);
        try
        {
            var until = Stopwatch.StartNew();
            while (true)
            {
                var history = await new SqliteEvidenceReconciliationQuery(options).ReadAsync(new());
                if (history.Records.Count > 0) break;
                Assert.True(until.Elapsed < TimeSpan.FromSeconds(5));
                await Task.Delay(10);
            }
            Assert.False(worker.Startup.IsCompleted);
            local.SetResult(true);
            await worker.Startup.WaitAsync(TimeSpan.FromSeconds(5));
            await worker.RunScrubTurnAsync();
            var snapshot = await new SqliteEvidenceReconciliationQuery(options).ReadAsync(new());
            Assert.True(snapshot.Available, snapshot.ReasonCode);
            Assert.True(snapshot.LatestStartup!.Completed);
            Assert.True(snapshot.Scrubber!.Completed);
            Assert.False(snapshot.IntegrityFaultRecorded);
            Assert.Empty(faults);
            Assert.Empty((await new SqliteProductionOutboxQuery(options).ReadHistoryAsync()).Events);
        }
        finally { Assert.True(await worker.StopAsync()); }
    }

    private static EvidenceReconciliationPayload ReconciliationStart(EvidenceReconciliationStream? stream, long through) =>
        new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            stream is null ? EvidenceReconciliationPhase.Startup : EvidenceReconciliationPhase.HistoricalScrub,
            EvidenceReconciliationEventKind.RunStarted, stream, DateTimeOffset.UtcNow, "VerificationRunStarted",
            null, null, through, 0, 0, 0, 0, 0);
    private static ValueTask<EvidenceReconciliationWriteResult> AppendReconciliationFactsAsync(
        SqliteCommandStore store, IReadOnlyList<EvidenceReconciliationPayload> facts)
    {
        var now = DateTimeOffset.UtcNow;
        return store.AppendEvidenceReconciliationAsync(facts.Select(x => x with { RecordedAtUtc = now }).ToArray(),
            null, ReconciliationDeadline());
    }
    private static StoreDeadline ReconciliationDeadline() => new(TimeSpan.FromSeconds(15));
    private static async Task MigrateReconciliationAsync(ProductionStoreOptions target)
    {
        var opened = await SqliteStartupMaintenance.OpenAsync(target,
            new StoreStartupMaintenanceOptions(typeof(SqliteStartupMaintenance).Assembly.Location)
            { MaximumDatabaseBytes = 64L * 1024 * 1024, OperationTimeout = TimeSpan.FromSeconds(30) });
        Assert.True(opened.Available, opened.Status.ReasonCode);
        await using var session = opened.Session!;
        var completed = await StoreStartupMaintenanceTests.Finish(session);
        Assert.True(completed.Completed, completed.ReasonCode);
    }
    private static async Task CloseReconciliationSourceCycleAsync(OutboxCoreFixture fixture)
    {
        foreach (var kind in new[] { ProductionInspectionEventKind.PublicationPrepared,
            ProductionInspectionEventKind.ResultValidRaised, ProductionInspectionEventKind.ResultAcknowledged,
            ProductionInspectionEventKind.ResultValidCleared, ProductionInspectionEventKind.AcknowledgementReset })
        {
            var result = await fixture.Store.AppendProductionInspectionEventAsync(new(fixture.Core.Admission.InspectionId,
                kind, "V154LocalCycleClosure", DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()), fixture.Deadline());
            Assert.True(result.Committed, result.ReasonCode);
        }
    }
}
