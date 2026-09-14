using SharpInspect.Abstractions;
using SharpInspect.Runtime.Evidence;
using SharpInspect.Runtime.Outbox;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact, Trait("VerificationId", "V154_R06")]
    public async Task V154_R06_BusyUnknownOutcomeWriteCannotReleaseLocalStartupOrSend()
    {
        await using var fixture = await OutboxCoreFixture.CreateAsync();
        await fixture.CommitAsync();
        await CloseReconciliationSourceCycleAsync(fixture);
        var delivery = fixture.Batch.Deliveries[0];
        var attempt = Guid.NewGuid();
        var begun = await fixture.Store.BeginOutboxAttemptAsync(new(delivery.DeliveryId, attempt, 1,
            Guid.NewGuid(), DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("V154 blocked writer")), fixture.Deadline());
        Assert.True(begun.Committed, begun.ReasonCode);
        await fixture.Store.DisposeAsync();
        var target = ProductionOutboxMigrationTests.WithReconciliation(fixture.Options,
            Path.GetDirectoryName(fixture.Options.DatabasePath)!);
        await MigrateReconciliationAsync(target);
        await using var store = new SqliteCommandStore(target);
        Assert.True((await store.Initialization).Committed);
        var transport = new RuntimeHeldOutboxTransport(fixture.Receiver);
        var faults = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var blocker = SqliteNative.Open(target.DatabasePath, readOnly: false);
        SqliteNative.Execute(blocker.Handle!, "BEGIN IMMEDIATE;", ReconciliationDeadline());
        var worker = new ProductionOutboxWorker(store, target, new(new[] { fixture.Receiver.Binding(transport) }),
            Guid.NewGuid(), store.Initialization, _ => { }, reason => { faults.Enqueue(reason); return Task.CompletedTask; });
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => worker.Startup.WaitAsync(TimeSpan.FromSeconds(15)));
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(worker.Startup.IsCompletedSuccessfully);
            Assert.False(transport.Entered.Task.IsCompleted);
            Assert.NotEmpty(faults);
            var pending = await new SqliteProductionOutboxQuery(target).ReadPendingAsync();
            Assert.True(pending.Available, pending.ReasonCode);
            Assert.Equal(attempt, Assert.Single(pending.Items).ActiveAttemptId);
            Assert.Equal(0L, fixture.Receiver.EffectCount());
        }
        finally
        {
            SqliteNative.Execute(blocker.Handle!, "ROLLBACK;", ReconciliationDeadline());
            transport.Release.TrySetResult(true);
            Assert.True(await worker.StopAsync());
        }
    }

    [Fact, Trait("VerificationId", "V154_R03")]
    public async Task V154_R03_RuntimeCannotBecomeReadyBeforeItsCurrentStartupProofCompletes()
    {
        using var receiver = new OutboxReceiverFixture();
        var outbox = new ProductionOutboxStoreOptions(new[] { receiver.Route });
        var policy = OutboxPolicy(outbox);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        ManualHarness? harness = null;
        try
        {
            harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
                outbox: outbox, outboxTransports: new(new[] { receiver.Binding(new RuntimeRejectedOutboxTransport()) }),
                tracePolicy: policy, evidenceReconciliation: new(policy.Scrubber),
                beforeRuntime: store => store.EvidenceReconciliationAfterReadProof = () =>
                {
                    if (Interlocked.Increment(ref calls) != 1) return;
                    entered.TrySetResult(true);
                    if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("V154 startup release missing");
                });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
            var query = harness.Service<IEvidenceReconciliationQuery>();
            var before = await query.ReadAsync(new());
            Assert.True(before.Available, before.ReasonCode);
            Assert.Null(before.LatestStartup);
            release.Set();
            using var issuer = new ProductionTestIssuer();
            await PrepareProductionAsync(harness, issuer);
            await ArmProductionAsync(harness);
            await WaitProductionAsync(harness, state => state.Ready, "V154 current startup permits Ready");
            var completed = await query.ReadAsync(new());
            Assert.True(completed.Available, completed.ReasonCode);
            Assert.True(completed.LatestStartup!.Completed);
            Assert.False(completed.IntegrityFaultRecorded);
            var preflight = await harness.Service<ITraceStoragePolicyService>().GetPreflightAsync();
            foreach (var gate in new[] { TraceStoragePreflightGate.EvidenceReconciliation, TraceStoragePreflightGate.Scrubber })
                Assert.Equal(TraceStoragePreflightStatus.Passed, Assert.Single(preflight.Rows, x => x.Gate == gate).Status);
            Assert.Equal((await harness.Runtime.GetSnapshotAsync()).RuntimeEpoch,
                Assert.Single(completed.Records, x => x.Kind == EvidenceReconciliationEventKind.RunStarted).RuntimeEpoch);
        }
        finally
        {
            release.Set();
            if (harness is not null) await harness.DisposeAsync();
        }
    }

    [Theory, Trait("VerificationId", "V154_R04")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V154_R04_InterruptedExternalAttemptIsLocallyUnknownBeforeTheSendGateOpens(bool alreadyAccepted)
    {
        await using var fixture = await OutboxCoreFixture.CreateAsync(route => new(new[] { route })
            { MaximumRetryDelay = TimeSpan.FromMilliseconds(10) });
        await fixture.CommitAsync();
        await CloseReconciliationSourceCycleAsync(fixture);
        var delivery = fixture.Batch.Deliveries[0];
        var oldAttempt = Guid.NewGuid(); var oldEpoch = Guid.NewGuid();
        var begun = await fixture.Store.BeginOutboxAttemptAsync(new(delivery.DeliveryId, oldAttempt, 1,
            oldEpoch, DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("V154 interrupted sender")), fixture.Deadline());
        Assert.True(begun.Committed, begun.ReasonCode);
        if (alreadyAccepted) fixture.Receiver.Accept(delivery);
        await fixture.Store.DisposeAsync();
        var target = ProductionOutboxMigrationTests.WithReconciliation(fixture.Options,
            Path.GetDirectoryName(fixture.Options.DatabasePath)!);
        await MigrateReconciliationAsync(target);
        await using var store = new SqliteCommandStore(target);
        Assert.True((await store.Initialization).Committed);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RuntimeHeldOutboxTransport(fixture.Receiver);
        transport.Release.TrySetResult(true);
        var faults = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Task Fault(string reason) { faults.Enqueue(reason); return Task.CompletedTask; }
        var sender = new ProductionOutboxWorker(store, target, new(new[] { fixture.Receiver.Binding(transport) }),
            Guid.NewGuid(), store.Initialization, _ => { }, Fault, gate.Task);
        EvidenceReconciliationWorker? reconciliation = null;
        try
        {
            await sender.Startup.WaitAsync(TimeSpan.FromSeconds(15));
            var query = new SqliteProductionOutboxQuery(target);
            var local = await query.ReadHistoryAsync(deliveryId: delivery.DeliveryId);
            Assert.True(local.Available, local.ReasonCode);
            var unknown = Assert.Single(local.Events, x => x.Kind == OutboxEventKind.AttemptFailed);
            Assert.Equal(oldAttempt, unknown.AttemptId);
            Assert.Equal(oldEpoch, unknown.RuntimeEpoch);
            Assert.Equal(OutboxFailureCategory.UnknownOutcome, unknown.FailureCategory);
            Assert.False(transport.Entered.Task.IsCompleted);
            reconciliation = new(store, target, Guid.NewGuid(), store.Initialization, sender.Startup, Fault,
                startScrubber: false);
            await reconciliation.Startup.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(transport.Entered.Task.IsCompleted);
            gate.SetResult(true);
            await WaitOutboxEventAsync(query, delivery.DeliveryId, OutboxEventKind.Succeeded, () => sender.FailureReason);
            Assert.Equal(delivery.DeliveryId, transport.Delivery!.DeliveryId);
            Assert.Equal(delivery.Payload!.CopyBytes(), transport.Delivery.Payload!.CopyBytes());
            Assert.Equal(1L, fixture.Receiver.EffectCount());
            Assert.Empty(faults);
        }
        finally
        {
            if (reconciliation is not null) Assert.True(await reconciliation.StopAsync());
            Assert.True(await sender.StopAsync());
        }
    }

    [Fact, Trait("VerificationId", "V154_R05")]
    public async Task V154_R05_HistoricalPageBudgetResumesTheSameFrozenRunAfterAColdRestart()
    {
        await using var fixture = await OutboxCoreFixture.CreateAsync(route => new(new[]
        {
            route, new OutboxRouteDefinition("V154.second", route.Version, OutboxRouteCriticality.BestEffort,
                route.DestinationIdentity, route.PayloadContract, route.ContentType, route.ReceiverContract,
                route.ReceiverPublicKeyBase64, route.AdapterContract, route.MaximumPayloadBytes)
        }));
        await fixture.CommitAsync();
        await CloseReconciliationSourceCycleAsync(fixture);
        var before = fixture.Batch.Deliveries.ToDictionary(x => x.DeliveryId, x => x.Payload!.CopyBytes());
        var originalEvents = (await fixture.Query.ReadHistoryAsync()).Events.Select(x => x.ContentHash).ToArray();
        await fixture.Store.DisposeAsync();
        var target = ProductionOutboxMigrationTests.WithReconciliation(fixture.Options,
            Path.GetDirectoryName(fixture.Options.DatabasePath)!);
        typeof(ProductionStoreOptions).GetProperty(nameof(ProductionStoreOptions.EvidenceReconciliation))!
            .SetValue(target, new EvidenceReconciliationStoreOptions(new(TimeSpan.FromMinutes(5),
                TimeSpan.FromSeconds(20), 1024 * 1024, 1)));
        await MigrateReconciliationAsync(target);
        var firstEpoch = Guid.NewGuid();
        Guid runId; long frozenTail; long after;
        await using (var store = new SqliteCommandStore(target))
        {
            Assert.True((await store.Initialization).Committed);
            var worker = new EvidenceReconciliationWorker(store, target, firstEpoch, store.Initialization,
                Task.CompletedTask, reason => throw new InvalidOperationException(reason), startScrubber: false);
            try
            {
                await worker.Startup.WaitAsync(TimeSpan.FromSeconds(20));
                await worker.RunScrubTurnAsync();
                var snapshot = await new SqliteEvidenceReconciliationQuery(target).ReadAsync(new());
                Assert.True(snapshot.Available, snapshot.ReasonCode);
                var progress = snapshot.Scrubber!;
                Assert.False(progress.Completed);
                Assert.Equal(1, progress.ScannedItems);
                Assert.Equal(1, progress.VerifiedItems);
                runId = progress.RunId; frozenTail = progress.ThroughSourcePosition; after = progress.AfterSourcePosition;
                Assert.True(after < frozenTail);
            }
            finally { Assert.True(await worker.StopAsync()); }
        }
        await using (var reopened = new SqliteCommandStore(target))
        {
            var initialized = await reopened.Initialization;
            Assert.True(initialized.Committed, initialized.ReasonCode);
            var worker = new EvidenceReconciliationWorker(reopened, target, Guid.NewGuid(), reopened.Initialization,
                Task.CompletedTask, reason => throw new InvalidOperationException(reason), startScrubber: false);
            try
            {
                await worker.Startup.WaitAsync(TimeSpan.FromSeconds(20));
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunScrubTurnAsync(cancelled.Token));
                var unchanged = await new SqliteEvidenceReconciliationQuery(target).ReadAsync(new());
                Assert.Equal(after, unchanged.Scrubber!.AfterSourcePosition);
                await worker.RunScrubTurnAsync();
                var snapshot = await new SqliteEvidenceReconciliationQuery(target).ReadAsync(new());
                Assert.True(snapshot.Available, snapshot.ReasonCode);
                var progress = snapshot.Scrubber!;
                Assert.True(progress.Completed);
                Assert.Equal(runId, progress.RunId);
                Assert.Equal(frozenTail, progress.ThroughSourcePosition);
                Assert.Equal(frozenTail, progress.AfterSourcePosition);
                Assert.Equal(2, progress.ScannedItems);
                Assert.Equal(2, progress.VerifiedItems);
                Assert.Equal(0, progress.DeferredItems);
                Assert.Equal(2, snapshot.Records.Count(x => x.RunId == runId &&
                    x.Kind == EvidenceReconciliationEventKind.OutboxVerified));
                Assert.Equal(originalEvents, (await new SqliteProductionOutboxQuery(target).ReadHistoryAsync())
                    .Events.Select(x => x.ContentHash).ToArray());
                foreach (var pending in (await new SqliteProductionOutboxQuery(target).ReadPendingAsync()).Items)
                    Assert.Equal(before[pending.Delivery.DeliveryId], pending.Delivery.Payload!.CopyBytes());
            }
            finally { Assert.True(await worker.StopAsync()); }
        }
    }
}
